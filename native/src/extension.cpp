/**
 * =============================================================================
 * CS2VoiceRelay voice tap
 * Copyright (C) 2024 Poggu, 2026 vindict6
 * =============================================================================
 *
 * This program is free software; you can redistribute it and/or modify it under
 * the terms of the GNU General Public License, version 3.0, as published by the
 * Free Software Foundation.
 *
 * This program is distributed in the hope that it will be useful, but WITHOUT
 * ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS
 * FOR A PARTICULAR PURPOSE.  See the GNU General Public License for more
 * details.
 *
 * You should have received a copy of the GNU General Public License along with
 * this program.  If not, see <http://www.gnu.org/licenses/>.
 */

// CS2's client stops playing server voice after a workshop addon unloads (Valve issue
// #3980); nothing a server sends can repair that. This tap copies every svc_VoiceData the
// engine sends to a player, after the engine has applied all of its voice routing, and
// forwards it over localhost UDP to the CS2VoiceRelay CounterStrikeSharp plugin, which
// streams it to that player's browser. For players who are listening in a browser it can
// also hold back the in-game copy so they don't hear everyone twice.

#ifdef _WIN32
#define WIN32_LEAN_AND_MEAN
#include <winsock2.h>
#include <ws2tcpip.h>
#else
#include <arpa/inet.h>
#include <netinet/in.h>
#include <sys/socket.h>
#include <sys/time.h>
#include <unistd.h>
#endif

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <atomic>
#include <chrono>
#include <string>
#include <thread>
#include "extension.h"
#include <iserver.h>
#include "utils/module.h"
#include "interfaces.h"
#include <netmessages.pb.h>
#include <google/protobuf/message.h>
#include <google/protobuf/descriptor.h>
#include "networksystem/inetworkmessages.h"
#include "networksystem/netmessage.h"
#include "cs2_sdk/serversideclient.h"

#ifdef _WIN32
#define ROOTBIN "/bin/win64/"
#define GAMEBIN "/csgo/bin/win64/"
typedef SOCKET tap_socket_t;
#define TAP_INVALID_SOCKET INVALID_SOCKET
#define tap_closesocket closesocket
#else
#define ROOTBIN "/bin/linuxsteamrt64/"
#define GAMEBIN "/csgo/bin/linuxsteamrt64/"
typedef int tap_socket_t;
#define TAP_INVALID_SOCKET (-1)
#define tap_closesocket close
#endif

#undef max
#undef min

constexpr int MAX_PLAYER_SLOTS = 64;
constexpr uint16_t DEFAULT_RELAY_PORT = 27031;

CS2VoiceRelayTap g_CS2VoiceRelayTap;

// Known-good CServerSideClient::SendNetMessage vtable slot. The hook derives its
// slot from the class declaration; Load refuses to hook if the two disagree, so
// a stale header can never redirect the hook onto the wrong function.
#ifdef _WIN32
constexpr int g_iSendNetMessageOffset = 15;
#else
constexpr int g_iSendNetMessageOffset = 16;
#endif

static void* g_pServerSideClientVTable = nullptr;

// SendNetMessage can be called off the main thread; KHook swaps the vtable slot
// (no code patching) and its dispatch is thread-safe
KHook::Virtual<CServerSideClientBase, bool, const CNetMessage*, NetChannelBufType_t> g_hookSendNetMessage(&CServerSideClientBase::SendNetMessage, &g_CS2VoiceRelayTap, &CS2VoiceRelayTap::Hook_SendNetMessage, nullptr);

// ---------------------------------------------------------------------------- localhost link

// tap -> plugin   "CVT1" voice packet (see BuildVoicePacket), "CVH1" heartbeat
// plugin -> tap   "CVC1" u64 suppress mask (bit = player slot), u32 valid_ms
static tap_socket_t g_socket = TAP_INVALID_SOCKET;
static sockaddr_in g_relayAddr;
static std::atomic<bool> g_running{false};
static std::thread g_controlThread;
static std::atomic<uint64_t> g_suppressMask{0};
static std::atomic<int64_t> g_suppressUntilMs{0};
static std::atomic<uint32_t> g_sent{0};

static int64_t NowMs()
{
	return std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now().time_since_epoch()).count();
}

static void SendToRelay(const uint8_t* data, size_t len)
{
	if (g_socket == TAP_INVALID_SOCKET)
		return;
	sendto(g_socket, (const char*)data, (int)len, 0, (const sockaddr*)&g_relayAddr, sizeof(g_relayAddr));
}

static void ControlLoop()
{
	uint8_t buf[64];
	int64_t nextHeartbeat = 0;
	while (g_running.load())
	{
		int64_t now = NowMs();
		if (now >= nextHeartbeat)
		{
			// Lets the plugin learn our port even while nobody is talking
			SendToRelay((const uint8_t*)"CVH1", 4);
			nextHeartbeat = now + 1000;
		}

		sockaddr_in from;
#ifdef _WIN32
		int fromLen = sizeof(from);
#else
		socklen_t fromLen = sizeof(from);
#endif
		int n = recvfrom(g_socket, (char*)buf, sizeof(buf), 0, (sockaddr*)&from, &fromLen);
		if (n < 16 || memcmp(buf, "CVC1", 4) != 0)
			continue;
		// Only the plugin on this machine may change routing
		if (from.sin_addr.s_addr != htonl(INADDR_LOOPBACK))
			continue;

		uint64_t mask;
		uint32_t validMs;
		memcpy(&mask, buf + 4, 8);
		memcpy(&validMs, buf + 12, 4);
		if (validMs > 10000)
			validMs = 10000;
		g_suppressMask.store(mask);
		g_suppressUntilMs.store(NowMs() + validMs);
	}
}

static bool OpenLink(char* error, size_t maxlen)
{
#ifdef _WIN32
	WSADATA wsa;
	if (WSAStartup(MAKEWORD(2, 2), &wsa) != 0)
	{
		V_snprintf(error, maxlen, "WSAStartup failed");
		return false;
	}
#endif
	uint16_t port = DEFAULT_RELAY_PORT;
	if (const char* env = getenv("CS2VR_TAP_PORT"))
	{
		int p = atoi(env);
		if (p > 0 && p < 65536)
			port = (uint16_t)p;
	}

	g_socket = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
	if (g_socket == TAP_INVALID_SOCKET)
	{
		V_snprintf(error, maxlen, "Could not create UDP socket");
		return false;
	}

	sockaddr_in local = {};
	local.sin_family = AF_INET;
	local.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
	local.sin_port = 0;
	if (bind(g_socket, (sockaddr*)&local, sizeof(local)) != 0)
	{
		V_snprintf(error, maxlen, "Could not bind UDP socket to localhost");
		tap_closesocket(g_socket);
		g_socket = TAP_INVALID_SOCKET;
		return false;
	}

	// Short receive timeout so the control thread can notice Unload
#ifdef _WIN32
	DWORD timeout = 500;
	setsockopt(g_socket, SOL_SOCKET, SO_RCVTIMEO, (const char*)&timeout, sizeof(timeout));
#else
	timeval timeout = {0, 500 * 1000};
	setsockopt(g_socket, SOL_SOCKET, SO_RCVTIMEO, &timeout, sizeof(timeout));
#endif

	memset(&g_relayAddr, 0, sizeof(g_relayAddr));
	g_relayAddr.sin_family = AF_INET;
	g_relayAddr.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
	g_relayAddr.sin_port = htons(port);

	g_running.store(true);
	g_controlThread = std::thread(ControlLoop);
	ConMsg("[CS2VoiceRelayTap] forwarding voice to 127.0.0.1:%u\n", port);
	return true;
}

static void CloseLink()
{
	g_running.store(false);
	if (g_controlThread.joinable())
		g_controlThread.join();
	if (g_socket != TAP_INVALID_SOCKET)
	{
		tap_closesocket(g_socket);
		g_socket = TAP_INVALID_SOCKET;
	}
	g_suppressMask.store(0);
#ifdef _WIN32
	WSACleanup();
#endif
}

// ---------------------------------------------------------------------------- voice message access

// Fields are resolved against the engine's own descriptors and read through
// reflection, so the plugin never depends on the compiled message layout.
struct VoiceFields
{
	const google::protobuf::FieldDescriptor* audio = nullptr;
	const google::protobuf::FieldDescriptor* xuid = nullptr;
	const google::protobuf::FieldDescriptor* entity = nullptr;
	const google::protobuf::FieldDescriptor* clientDeprecated = nullptr;
	// CMsgVoiceAudio
	const google::protobuf::FieldDescriptor* format = nullptr;
	const google::protobuf::FieldDescriptor* voiceData = nullptr;
	const google::protobuf::FieldDescriptor* sampleRate = nullptr;
	const google::protobuf::FieldDescriptor* sectionNumber = nullptr;
	const google::protobuf::FieldDescriptor* sequenceBytes = nullptr;
	const google::protobuf::FieldDescriptor* packetOffsets = nullptr;
	bool ok = false;
};

static const google::protobuf::FieldDescriptor* FindField(const google::protobuf::Descriptor* desc, const char* name, google::protobuf::FieldDescriptor::CppType type, bool repeated = false)
{
	if (!desc)
		return nullptr;
	const google::protobuf::FieldDescriptor* field = desc->FindFieldByName(name);
	if (!field || field->is_repeated() != repeated || field->cpp_type() != type)
		return nullptr;
	return field;
}

static VoiceFields ResolveVoiceFields(const google::protobuf::Descriptor* desc)
{
	using FD = google::protobuf::FieldDescriptor;

	VoiceFields f;
	f.audio = FindField(desc, "audio", FD::CPPTYPE_MESSAGE);
	f.xuid = FindField(desc, "xuid", FD::CPPTYPE_UINT64);
	f.entity = FindField(desc, "entity", FD::CPPTYPE_INT32);
	f.clientDeprecated = FindField(desc, "client_deprecated", FD::CPPTYPE_INT32);

	const google::protobuf::Descriptor* audio = f.audio ? f.audio->message_type() : nullptr;
	f.format = FindField(audio, "format", FD::CPPTYPE_ENUM);
	f.voiceData = FindField(audio, "voice_data", FD::CPPTYPE_STRING);
	f.sampleRate = FindField(audio, "sample_rate", FD::CPPTYPE_UINT32);
	f.sectionNumber = FindField(audio, "section_number", FD::CPPTYPE_UINT32);
	f.sequenceBytes = FindField(audio, "sequence_bytes", FD::CPPTYPE_INT32);
	f.packetOffsets = FindField(audio, "packet_offsets", FD::CPPTYPE_UINT32, true);

	f.ok = f.audio && f.xuid && f.voiceData;
	if (!f.ok)
		ConMsg("[CS2VoiceRelayTap] CSVCMsg_VoiceData layout not recognised (audio %d, xuid %d, voice_data %d); voice tap disabled\n",
			f.audio != nullptr, f.xuid != nullptr, f.voiceData != nullptr);
	return f;
}

template <typename T>
static void Put(uint8_t*& p, T v)
{
	memcpy(p, &v, sizeof(T));
	p += sizeof(T);
}

/**
 * "CVT1" voice packet, little-endian:
 *   u8 recipient_slot, u64 speaker_xuid, i32 speaker_entity, i32 speaker_client,
 *   u8 format, u32 sample_rate, u32 section_number, i32 sequence_bytes,
 *   u16 offset_count, u32 offsets[offset_count], u16 data_len, u8 data[data_len]
 */
static void ForwardVoice(int recipient, const google::protobuf::Message& msg, const VoiceFields& f)
{
	const google::protobuf::Reflection* refl = msg.GetReflection();
	if (!refl->HasField(msg, f.audio))
		return;

	const google::protobuf::Message& audio = refl->GetMessage(msg, f.audio);
	const google::protobuf::Reflection* ar = audio.GetReflection();

	std::string scratch;
	const std::string& data = ar->GetStringReference(audio, f.voiceData, &scratch);
	int offsetCount = f.packetOffsets ? ar->FieldSize(audio, f.packetOffsets) : 0;
	if (data.empty() || data.size() > 4096 || offsetCount > 64)
		return;

	uint8_t buf[4 + 1 + 8 + 4 + 4 + 1 + 4 + 4 + 4 + 2 + 64 * 4 + 2 + 4096];
	uint8_t* p = buf;
	memcpy(p, "CVT1", 4);
	p += 4;
	Put<uint8_t>(p, (uint8_t)recipient);
	Put<uint64_t>(p, refl->GetUInt64(msg, f.xuid));
	Put<int32_t>(p, f.entity ? refl->GetInt32(msg, f.entity) : -1);
	Put<int32_t>(p, f.clientDeprecated ? refl->GetInt32(msg, f.clientDeprecated) : -1);
	Put<uint8_t>(p, (uint8_t)(f.format ? ar->GetEnumValue(audio, f.format) : 255));
	Put<uint32_t>(p, f.sampleRate ? ar->GetUInt32(audio, f.sampleRate) : 0);
	Put<uint32_t>(p, f.sectionNumber ? ar->GetUInt32(audio, f.sectionNumber) : 0);
	Put<int32_t>(p, f.sequenceBytes ? ar->GetInt32(audio, f.sequenceBytes) : 0);
	Put<uint16_t>(p, (uint16_t)offsetCount);
	for (int i = 0; i < offsetCount; i++)
		Put<uint32_t>(p, ar->GetRepeatedUInt32(audio, f.packetOffsets, i));
	Put<uint16_t>(p, (uint16_t)data.size());
	memcpy(p, data.data(), data.size());
	p += data.size();

	SendToRelay(buf, (size_t)(p - buf));
}

KHook::Return<bool> CS2VoiceRelayTap::Hook_SendNetMessage(CServerSideClientBase* pClient, const CNetMessage* pData, NetChannelBufType_t bufType)
{
	if (!pClient || !pData)
		return {KHook::Action::Ignore};

	INetworkMessageInternal* pNetMessage = pData->GetNetMessage();
	NetMessageInfo_t* info = pNetMessage ? pNetMessage->GetNetMessageInfo() : nullptr;
	if (!info || info->m_MessageId != SVC_Messages::svc_VoiceData)
		return {KHook::Action::Ignore};

	int slot = pClient->GetPlayerSlot().Get();
	if (slot < 0 || slot >= MAX_PLAYER_SLOTS)
		return {KHook::Action::Ignore};

	auto* msg = static_cast<const google::protobuf::Message*>(pData->AsProto());
	if (!msg)
		return {KHook::Action::Ignore};

	const google::protobuf::Descriptor* desc = msg->GetDescriptor();
	if (!desc || desc->full_name() != "CSVCMsg_VoiceData")
		return {KHook::Action::Ignore};

	static const VoiceFields s_fields = ResolveVoiceFields(desc);
	if (!s_fields.ok)
		return {KHook::Action::Ignore};

	ForwardVoice(slot, *msg, s_fields);

	uint32_t n = g_sent.fetch_add(1, std::memory_order_relaxed);
	if (n == 0)
		ConMsg("[CS2VoiceRelayTap] first voice packet forwarded (recipient slot %d)\n", slot);

	// The player is listening in a browser: don't also play it in game. The plugin
	// refreshes the mask continuously, so this lapses on its own if the plugin stops.
	if ((g_suppressMask.load(std::memory_order_relaxed) >> slot) & 1)
	{
		if (NowMs() < g_suppressUntilMs.load(std::memory_order_relaxed))
			return {KHook::Action::Supercede, true};
	}

	return {KHook::Action::Ignore};
}

static bool SetupHook(char* error, size_t maxlen)
{
	CModule engineModule(ROOTBIN, "engine2");

	g_pServerSideClientVTable = engineModule.FindVirtualTable("CServerSideClient");
	if (!g_pServerSideClientVTable)
	{
		V_snprintf(error, maxlen, "Could not find CServerSideClient vtable");
		return false;
	}

	int index = KHook::GetVtableIndex(&CServerSideClientBase::SendNetMessage);
	if (index != g_iSendNetMessageOffset)
	{
		V_snprintf(error, maxlen, "SendNetMessage vtable index mismatch (header %d, expected %d)", index, g_iSendNetMessageOffset);
		g_pServerSideClientVTable = nullptr;
		return false;
	}

	g_hookSendNetMessage.AddGlobal((CServerSideClientBase*)&g_pServerSideClientVTable);

	ConMsg("[CS2VoiceRelayTap] SendNetMessage hook installed (vtable index %d)\n", index);
	return true;
}

PLUGIN_EXPOSE(CS2VoiceRelayTap, g_CS2VoiceRelayTap);
bool CS2VoiceRelayTap::Load(PluginId id, ISmmAPI *ismm, char *error, size_t maxlen, bool late)
{
	PLUGIN_SAVEVARS();

	GET_V_IFACE_CURRENT(GetEngineFactory, Interfaces::engine, IVEngineServer, INTERFACEVERSION_VENGINESERVER);
	GET_V_IFACE_CURRENT(GetEngineFactory, Interfaces::icvar, ICvar, CVAR_INTERFACE_VERSION);
	GET_V_IFACE_ANY(GetServerFactory, Interfaces::server, IServerGameDLL, INTERFACEVERSION_SERVERGAMEDLL);
	GET_V_IFACE_ANY(GetServerFactory, Interfaces::gameclients, IServerGameClients, INTERFACEVERSION_SERVERGAMECLIENTS);

	if (!OpenLink(error, maxlen))
		return false;

	// Install the hook only once nothing else can fail: a failed Load unloads the
	// binary, and a hook left behind would point into unmapped memory
	if (!SetupHook(error, maxlen))
	{
		CloseLink();
		return false;
	}

	g_SMAPI->AddListener(this, this);
	return true;
}

bool CS2VoiceRelayTap::Unload(char *error, size_t maxlen)
{
	if (g_pServerSideClientVTable)
	{
		g_hookSendNetMessage.RemoveGlobal((CServerSideClientBase*)&g_pServerSideClientVTable);
		g_pServerSideClientVTable = nullptr;
	}
	CloseLink();
	return true;
}

void CS2VoiceRelayTap::AllPluginsLoaded()
{
}

bool CS2VoiceRelayTap::Pause(char *error, size_t maxlen)
{
	return true;
}

bool CS2VoiceRelayTap::Unpause(char *error, size_t maxlen)
{
	return true;
}

const char *CS2VoiceRelayTap::GetLicense()
{
	return "GPLv3";
}

const char *CS2VoiceRelayTap::GetVersion()
{
	return "2.0.0";
}

const char *CS2VoiceRelayTap::GetDate()
{
	return __DATE__;
}

const char *CS2VoiceRelayTap::GetLogTag()
{
	return "CS2VoiceRelayTap";
}

const char *CS2VoiceRelayTap::GetAuthor()
{
	return "vindict6";
}

const char *CS2VoiceRelayTap::GetDescription()
{
	return "Forwards in-game voice to CS2VoiceRelay's browser player";
}

const char *CS2VoiceRelayTap::GetName()
{
	return "CS2VoiceRelayTap";
}

const char *CS2VoiceRelayTap::GetURL()
{
	return "https://github.com/vindict6/CS2VoiceRelay";
}
