using PeripheralBatteryDashboard.Core;

namespace PeripheralBatteryDashboard.Providers
{
    public static class BuiltInProviderCatalog
    {
        public static void RegisterInto(ProviderRegistry registry)
        {
            registry.Register(new SteelSeriesNova7Provider());
            registry.Register(new AulaF108Provider());
            registry.Register(new VxeR1Provider());
            registry.Register(new BluetoothGattBatteryProvider());
            registry.Register(new XboxControllerProvider());
        }
    }
}
