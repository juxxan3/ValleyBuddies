using System;
using System.Reflection;
using StardewModdingAPI;

namespace ValleyBuddies.Bridge;

/// <summary>Registers a dynamic prompt block through AliveNpcs's official API (falls back to Harmony modContext injection).</summary>
public sealed class PromptInjector
{
    private readonly IMonitor _monitor;
    private readonly Func<string, string> _provider;
    private IDisposable? _registration;

    public bool OfficialInjectionConfirmed { get; private set; }

    public PromptInjector(IMonitor monitor, Func<string, string> provider)
    {
        _monitor  = monitor;
        _provider = provider;
    }

    public void TryRegister(IModHelper helper, string ownerModId)
    {
        try
        {
            var api = helper.ModRegistry.GetApi("Lucas.AliveNpcs");
            if (api == null) { _monitor.Log("AliveNpcs API unavailable — using Harmony modContext injection.", LogLevel.Debug); return; }

            var method = api.GetType().GetMethod("RegisterDynamicPromptBlock");
            if (method == null) { _monitor.Log("RegisterDynamicPromptBlock not found in this AliveNpcs version.", LogLevel.Debug); return; }

            var parms = method.GetParameters();
            if (parms.Length != 1) return;

            var regType = parms[0].ParameterType;
            var reg = Activator.CreateInstance(regType);
            SetProp(regType, reg, "OwnerModId", ownerModId);
            SetProp(regType, reg, "BlockId",    "valleybuddyactions-physical");
            SetProp(regType, reg, "Target",     "evaluate_response");
            SetProp(regType, reg, "MaxChars",   3000);

            var provProp = regType.GetProperty("Provider");
            if (provProp != null)
            {
                var provMethod = typeof(PromptInjector).GetMethod(nameof(Provide), BindingFlags.Instance | BindingFlags.NonPublic);
                var del = Delegate.CreateDelegate(provProp.PropertyType, this, provMethod!);
                provProp.SetValue(reg, del);
            }

            _registration = method.Invoke(api, new[] { reg }) as IDisposable;
            _monitor.Log("Prompt block registered via official AliveNpcs API.", LogLevel.Debug);
        }
        catch (Exception ex)
        {
            _monitor.Log($"Official API registration failed ({ex.Message}) — using Harmony fallback.", LogLevel.Debug);
        }
    }

    private string Provide(object hookContext)
    {
        OfficialInjectionConfirmed = true;
        try { return _provider(ReactionInterceptor.CurrentNpcName ?? ""); }
        catch (Exception ex) { _monitor.Log("PromptInjector.Provide error: " + ex.Message, LogLevel.Trace); return ""; }
    }

    private static void SetProp(Type t, object? obj, string name, object val)
    {
        var p = t.GetProperty(name);
        if (p?.CanWrite == true) p.SetValue(obj, val);
    }

    public void Dispose()
    {
        try { _registration?.Dispose(); } catch { }
        _registration = null;
    }
}
