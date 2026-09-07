#define WIN32_LEAN_AND_MEAN
#include <winsock2.h>
#include <windows.h>
#include <ws2tcpip.h>
#include <iphlpapi.h>
#include <netioapi.h>
#include <cstddef>
#include <cstdio>
#include <cstring>

static_assert(sizeof(MIB_IF_ROW2) == 1352);
static_assert(offsetof(MIB_IF_ROW2, InterfaceIndex) == 8);
static_assert(offsetof(MIB_IF_ROW2, InterfaceGuid) == 12);
static_assert(offsetof(MIB_IF_ROW2, Type) == 1128);
static_assert(offsetof(MIB_IF_ROW2, InterfaceAndOperStatusFlags) == 1152);
static_assert(offsetof(MIB_IF_ROW2, OperStatus) == 1156);
static_assert(offsetof(MIB_IF_ROW2, NetworkGuid) == 1168);
static_assert(offsetof(MIB_IF_ROW2, ConnectionType) == 1184);
static_assert(offsetof(MIB_IF_ROW2, TransmitLinkSpeed) == 1192);
static_assert(offsetof(MIB_IF_ROW2, OutQLen) == 1344);

int main()
{
    MIB_IF_ROW2 row{};
    row.InterfaceAndOperStatusFlags.HardwareInterface = TRUE;
    row.InterfaceAndOperStatusFlags.ConnectorPresent = TRUE;
    unsigned char flags{};
    std::memcpy(&flags, &row.InterfaceAndOperStatusFlags, sizeof(flags));
    if (flags != 0x05) return 1;
    std::puts("PASS Windows SDK MIB_IF_ROW2 size, field offsets and hardware/connector bit layout.");
}
