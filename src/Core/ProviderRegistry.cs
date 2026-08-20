using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace PeripheralBatteryDashboard.Core
{
    public sealed class ProviderRegistry
    {
        private readonly Dictionary<string, IBatteryProvider> _providers =
            new Dictionary<string, IBatteryProvider>(StringComparer.OrdinalIgnoreCase);

        public IList<string> PluginWarnings { get; private set; }

        public ProviderRegistry()
        {
            PluginWarnings = new List<string>();
        }

        public void Register(IBatteryProvider provider)
        {
            if (provider == null || string.IsNullOrWhiteSpace(provider.ProviderId))
                throw new ArgumentException("유효하지 않은 배터리 공급자입니다.");
            if (_providers.ContainsKey(provider.ProviderId))
                throw new InvalidOperationException("중복 providerId: " + provider.ProviderId);
            _providers.Add(provider.ProviderId, provider);
        }

        public bool TryGet(string providerId, out IBatteryProvider provider)
        {
            return _providers.TryGetValue(providerId ?? string.Empty, out provider);
        }

        public IList<string> ProviderIds
        {
            get { return _providers.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(); }
        }

        public void LoadPlugins(string pluginDirectory)
        {
            PluginWarnings.Clear();
            if (!Directory.Exists(pluginDirectory))
                return;

            foreach (string file in Directory.GetFiles(pluginDirectory, "*.dll", SearchOption.AllDirectories))
            {
                try
                {
                    Assembly assembly = Assembly.LoadFrom(file);
                    Type[] types;
                    try { types = assembly.GetTypes(); }
                    catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }

                    foreach (Type type in types)
                    {
                        if (type.IsAbstract || !typeof(IBatteryProviderPlugin).IsAssignableFrom(type))
                            continue;
                        IBatteryProviderPlugin plugin = (IBatteryProviderPlugin)Activator.CreateInstance(type);
                        foreach (IBatteryProvider provider in plugin.CreateProviders() ?? Enumerable.Empty<IBatteryProvider>())
                            Register(provider);
                    }
                }
                catch (Exception ex)
                {
                    PluginWarnings.Add(Path.GetFileName(file) + ": " + ex.Message);
                }
            }
        }
    }
}
