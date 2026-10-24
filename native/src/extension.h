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

#ifndef _INCLUDE_CS2VOICERELAY_TAP_H_
#define _INCLUDE_CS2VOICERELAY_TAP_H_

#include <ISmmPlugin.h>
// Metamod's Source 2 headers no longer pull in eiface.h; it has to come before
// iserver.h, which relies on its forward declarations
#include <eiface.h>
#include <igameevents.h>
#include <playerslot.h>
#include "khook.hpp"
#include <inetchannel.h>

class CServerSideClientBase;
class CNetMessage;

class CS2VoiceRelayTap : public ISmmPlugin, public IMetamodListener
{
public:
	bool Load(PluginId id, ISmmAPI *ismm, char *error, size_t maxlen, bool late);
	bool Unload(char *error, size_t maxlen);
	bool Pause(char *error, size_t maxlen);
	bool Unpause(char *error, size_t maxlen);
	void AllPluginsLoaded();
public: //hooks
	KHook::Return<bool> Hook_SendNetMessage(CServerSideClientBase* pClient, const CNetMessage* pData, NetChannelBufType_t bufType);
public:
	const char *GetAuthor();
	const char *GetName();
	const char *GetDescription();
	const char *GetURL();
	const char *GetLicense();
	const char *GetVersion();
	const char *GetDate();
	const char *GetLogTag();
};

extern CS2VoiceRelayTap g_CS2VoiceRelayTap;

PLUGIN_GLOBALVARS();

#endif //_INCLUDE_CS2VOICERELAY_TAP_H_
