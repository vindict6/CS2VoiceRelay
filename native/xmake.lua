add_rules("mode.debug", "mode.release")

includes("@builtin/xpack")

local SDK_PATH = os.getenv("HL2SDKCS2")
local MM_PATH = os.getenv("MMSOURCE112")

target("CS2VoiceRelayTap")
    set_kind("shared")
    add_files("src/**.cpp|utils/plat_win.cpp|utils/plat_unix.cpp")
    add_headerfiles("src/**.h")
    set_symbols("hidden")

    if is_plat("windows") then
        set_runtimes("MT")
    end

    add_files({
        "protobufs/generated/netmessages.pb.cc",
        "protobufs/generated/networkbasetypes.pb.cc",
        "protobufs/generated/network_connection.pb.cc",
        "protobufs/generated/valveextensions.pb.cc",
        "protobufs/generated/source2_steam_stats.pb.cc",
    })

    if is_plat("windows") then
        add_links({
            SDK_PATH.."/lib/public/win64/2015/libprotobuf.lib",
            SDK_PATH.."/lib/public/win64/tier0.lib",
            SDK_PATH.."/lib/public/win64/interfaces.lib",
            SDK_PATH.."/lib/public/win64/mathlib.lib",
        })
    else
        add_links({
            SDK_PATH.."/lib/linux64/release/libprotobuf.a",
            SDK_PATH.."/lib/linux64/libtier0.so",
            SDK_PATH.."/lib/linux64/interfaces.a",
            SDK_PATH.."/lib/linux64/mathlib.a",
        })

        add_cxxflags("-lstdc++", "-Wno-register")
    end

    if is_plat("windows") then
        add_links("psapi", "ws2_32");
        add_files("src/utils/plat_win.cpp");
    else
        add_files("src/utils/plat_unix.cpp");
    end

    add_includedirs({
        "src",
        "protobufs/generated",
        -- sdk
        SDK_PATH,
        SDK_PATH.."/thirdparty/protobuf-3.21.8/src",
        SDK_PATH.."/common",
        SDK_PATH.."/game/shared",
        SDK_PATH.."/game/server",
        SDK_PATH.."/public",
        SDK_PATH.."/public/engine",
        SDK_PATH.."/public/mathlib",
        SDK_PATH.."/public/tier0",
        SDK_PATH.."/public/tier1",
        SDK_PATH.."/public/entity2",
        SDK_PATH.."/public/game/server",
        -- metamod
        MM_PATH.."/core",
        MM_PATH.."/third_party/khook/include",
    })

    if(is_plat("windows")) then
        add_defines({
            "COMPILER_MSVC",
            "COMPILER_MSVC64",
            "PLATFORM_64BITS",
            "WIN32",
            "WINDOWS",
            "CRT_SECURE_NO_WARNINGS",
            "CRT_SECURE_NO_DEPRECATE",
            "CRT_NONSTDC_NO_DEPRECATE",
            "_MBCS",
            "META_IS_SOURCE2"
        })
    else
        add_defines({
            "_LINUX",
            "LINUX",
            "POSIX",
            "GNUC",
            "COMPILER_GCC",
            "PLATFORM_64BITS",
            "META_IS_SOURCE2",
            "_GLIBCXX_USE_CXX11_ABI=0",
            -- The SDK's platform.h only maps _stricmp on POSIX, but its headers
            -- (e.g. tier0/logging.h) use the bare Windows spellings.
            "stricmp=strcasecmp",
            "_stricmp=strcasecmp",
            "_snprintf=snprintf",
            "_vsnprintf=vsnprintf",
            "HAVE_STDINT_H"
        })
    end
    set_languages("cxx20")
