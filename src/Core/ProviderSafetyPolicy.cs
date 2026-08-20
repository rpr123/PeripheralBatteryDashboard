using System;

namespace PeripheralBatteryDashboard.Core
{
    internal static class ProviderSafetyPolicy
    {
        internal static bool IsAllowedTransport(string transport)
        {
            return string.Equals(transport, "hid", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(transport, "bluetooth-gatt", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(transport, "xinput", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool RequiresExactHidSelector(string providerId)
        {
            return string.Equals(providerId, "builtin.steelseries.nova7",
                       StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(providerId, "builtin.aula.f108",
                       StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(providerId, "builtin.vxe.r1",
                       StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsBuiltInTransportCompatible(string providerId,
            string transport)
        {
            if (RequiresExactHidSelector(providerId))
                return string.Equals(transport, "hid", StringComparison.OrdinalIgnoreCase);
            if (string.Equals(providerId, "builtin.bluetooth.gatt-battery",
                StringComparison.OrdinalIgnoreCase))
            {
                return string.Equals(transport, "bluetooth-gatt",
                    StringComparison.OrdinalIgnoreCase);
            }
            if (string.Equals(providerId, "builtin.xbox.xinput",
                StringComparison.OrdinalIgnoreCase))
            {
                return string.Equals(transport, "xinput",
                    StringComparison.OrdinalIgnoreCase);
            }
            return true;
        }
    }
}
