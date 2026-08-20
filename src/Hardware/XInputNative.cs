using System.Runtime.InteropServices;

namespace PeripheralBatteryDashboard.Hardware
{
    internal static class XInputNative
    {
        internal const byte BATTERY_DEVTYPE_GAMEPAD = 0x00;
        internal const byte BATTERY_TYPE_DISCONNECTED = 0x00;
        internal const byte BATTERY_TYPE_WIRED = 0x01;
        internal const byte BATTERY_TYPE_ALKALINE = 0x02;
        internal const byte BATTERY_TYPE_NIMH = 0x03;
        internal const byte BATTERY_TYPE_UNKNOWN = 0xFF;

        internal const byte BATTERY_LEVEL_EMPTY = 0x00;
        internal const byte BATTERY_LEVEL_LOW = 0x01;
        internal const byte BATTERY_LEVEL_MEDIUM = 0x02;
        internal const byte BATTERY_LEVEL_FULL = 0x03;

        [StructLayout(LayoutKind.Sequential)]
        internal struct XINPUT_GAMEPAD
        {
            public ushort Buttons;
            public byte LeftTrigger;
            public byte RightTrigger;
            public short ThumbLX;
            public short ThumbLY;
            public short ThumbRX;
            public short ThumbRY;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct XINPUT_STATE
        {
            public uint PacketNumber;
            public XINPUT_GAMEPAD Gamepad;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct XINPUT_BATTERY_INFORMATION
        {
            public byte BatteryType;
            public byte BatteryLevel;
        }

        [DllImport("xinput1_4.dll", CallingConvention = CallingConvention.Winapi)]
        internal static extern uint XInputGetBatteryInformation(uint dwUserIndex, byte devType,
            out XINPUT_BATTERY_INFORMATION pBatteryInformation);

        [DllImport("xinput1_4.dll", CallingConvention = CallingConvention.Winapi)]
        internal static extern uint XInputGetState(uint dwUserIndex, out XINPUT_STATE pState);
    }
}
