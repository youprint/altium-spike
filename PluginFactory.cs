// PluginFactory.cs
//
// THE ENTRY POINT CONTRACT -- read from the working EasyEDA-Loader.dll
// installed on this machine, not inferred:
//
//     class CSharpPlugin.PluginFactory
//         public .ctor()                              <- parameterless
//         public Object InvokePluginFactory(IClient)  <- INSTANCE method
//
// Two things were wrong here before, and together they explain why
// DXP_Startup.log said "Server AltiumSpike started" while none of our
// code ever ran and no log file was created:
//
//   1. NAMESPACE. This type must be CSharpPlugin.PluginFactory. Altium
//      looks the type up by that exact fully-qualified name. Ours was
//      AltiumSpike.PluginFactory, so the lookup failed silently -- the
//      server record starts, the assembly is never really entered.
//      The original comment ("sourced verbatim from EasyEDALoader
//      CSharpPlugin.cs") was right about the file; the namespace got
//      changed to match the project and that broke discovery.
//
//   2. STATIC vs INSTANCE. EasyEDA-Loader's InvokePluginFactory is an
//      instance method with a public parameterless constructor -- Altium
//      constructs the factory, then calls the method on it. Ours was
//      static, which such a call cannot bind to.
//
// The module class itself keeps its own namespace; only the factory is
// bound by name. EasyEDA-Loader does exactly this: factory in
// CSharpPlugin, module in EasyEDA_Loader.EasyEDALoaderModule.

using System;
using System.Runtime.InteropServices;
using DXP;
using AltiumSpike;

namespace CSharpPlugin
{
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    public class PluginFactory
    {
        public PluginFactory()
        {
            Log.Write("CSharpPlugin.PluginFactory constructed");
        }

        public object InvokePluginFactory(IClient client)
        {
            Log.Write("=== InvokePluginFactory called ===");
            try
            {
                SpikeModule m = new SpikeModule(client);
                Log.Write("InvokePluginFactory returning SpikeModule OK");
                return m;
            }
            catch (Exception ex)
            {
                Log.Exception("InvokePluginFactory", ex);
                throw;
            }
        }
    }
}
