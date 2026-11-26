using System;
using System.Reflection;
using StardewModdingAPI;

namespace ValleyBuddies.Bridge;

/// <summary>Injects economic-style memories into the AliveNpcs gossip system.</summary>
public static class ValleyBuddiesMemoryFeed
{
    private static IMonitor?    _monitor;
    private static object?      _api;
    private static MethodInfo?  _injectGossip;
    private static string?      _ownerModId;

    public static bool IsActive   { get; private set; }
    public static int  SentToday  { get; private set; }

    public static void TryConnect(IModHelper helper, string ownerModId, IMonitor monitor)
    {
        _monitor    = monitor;
        _ownerModId = ownerModId;
        try
        {
            _api = helper.ModRegistry.GetApi("Lucas.AliveNpcs");
            if (_api == null) return;

            _injectGossip = _api.GetType().GetMethod("InjectGossip", new[] { typeof(string), typeof(string) });
            if (_injectGossip == null)
            {
                monitor.Log("InjectGossip not found in this AliveNpcs version.", LogLevel.Debug);
                return;
            }
            IsActive = true;
            monitor.Log("ValleyBuddies memory feed connected.", LogLevel.Debug);
        }
        catch (Exception ex)
        {
            monitor.Log("Memory feed connection failed: " + ex.Message, LogLevel.Trace);
        }
    }

    public static void ResetDailyCount() => SentToday = 0;

    public static void Send(string text, int dailyLimit)
    {
        if (!IsActive || string.IsNullOrWhiteSpace(text) || SentToday >= dailyLimit) return;
        try
        {
            var r = _injectGossip!.Invoke(_api, new object[] { _ownerModId!, text });
            bool accepted = (r is bool b && b) || (r is int i && i != 0);
            if (accepted)
            {
                SentToday++;
                _monitor?.Log("[Actions] Memory fed: " + text, LogLevel.Trace);
            }
            else
            {
                // If AliveNpcs returned false (e.g. daily limit hit), mark as full for today to avoid spamming
                SentToday = Math.Max(SentToday, dailyLimit);
            }
        }
        catch (Exception ex)
        {
            _monitor?.Log("Memory feed failed: " + ex.Message, LogLevel.Trace);
        }
    }
}
