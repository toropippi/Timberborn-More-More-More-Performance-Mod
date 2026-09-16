using System;
using System.Linq;
using System.Reflection;

namespace T3MPTestDriver;

internal static class StatusIconStagingExperiment
{
    internal static void Install()
    {
        if (!Environment.GetCommandLineArgs().Contains("-t3mpTestStatusIconStaging")) return;
        var installedType = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a != typeof(StatusIconStagingExperiment).Assembly)
            .Select(a => a.GetType("T3MP.Loading.PreparedStatusIcons")).FirstOrDefault(t => t != null);
        if (installedType?.GetProperty("Installed", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null) is true) return;
        T3MP.Loading.PreparedStatusIcons.Install("t3mp.test.status-icon-staging");
    }
}
