namespace ValleyBuddies.Systems;

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.Locations;
using ValleyBuddies.Multiplayer;

/// <summary>
/// Modelo de datos para una sesión de picnic activa en el mundo.
/// </summary>
public sealed class ActivePicnicSession
{
    public string LocationName { get; set; } = string.Empty;
    public Point OriginTile { get; set; }
    public string NpcName { get; set; } = string.Empty;
    public long OwnerFarmerId { get; set; }
    public string[] PropItemIds { get; set; } = Array.Empty<string>();
    public ulong StartedTick { get; set; }
    public ulong LastChatterTick { get; set; }
    public bool SnackGiven { get; set; }
    public int TotalStaminaGranted { get; set; }
    public ulong LastStaminaTick { get; set; }
}

/// <summary>
/// Servicio central de Picnic 2.0: renderizado efímero de manta a cuadros y utilería nativa debajo de los personajes,
/// búsqueda de suelo despejado 3x2 y sincronización multijugador.
/// </summary>
public sealed class PicnicSceneService
{
    public static PicnicSceneService Instance { get; } = new();

    private readonly Dictionary<string, ActivePicnicSession> _activeSessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _dailySnackGivenPairs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _dailyStaminaGrantedPairs = new(StringComparer.OrdinalIgnoreCase);
    private Texture2D? _blanketTexture;

    private static string BuildPairKey(long farmerId, string npcName) => $"{farmerId}|{npcName}";

    public bool TryConsumeDailySnackForPair(long farmerId, string npcName)
        => !string.IsNullOrWhiteSpace(npcName) && _dailySnackGivenPairs.Add(BuildPairKey(farmerId, npcName));

    public int TryConsumeDailyStamina(long farmerId, string npcName, int requested, int dailyMax = 25)
    {
        if (string.IsNullOrWhiteSpace(npcName) || requested <= 0) return 0;
        string k = BuildPairKey(farmerId, npcName);
        _dailyStaminaGrantedPairs.TryGetValue(k, out int used);
        int grant = Math.Clamp(requested, 0, Math.Max(0, dailyMax - used));
        if (grant > 0)
            _dailyStaminaGrantedPairs[k] = used + grant;
        return grant;
    }

    public bool HasActivePicnicForNpc(string npcName)
    {
        return !string.IsNullOrWhiteSpace(npcName) && _activeSessions.ContainsKey(npcName);
    }

    public ActivePicnicSession? GetSession(string npcName)
    {
        if (string.IsNullOrWhiteSpace(npcName)) return null;
        _activeSessions.TryGetValue(npcName, out var s);
        return s;
    }

    /// <summary>
    /// Intenta iniciar una sesión de picnic buscando un área 3x2 despejada cerca del NPC y jugador.
    /// </summary>
    public bool TryStartPicnic(GameLocation loc, NPC npc, Farmer owner, out Point originTile)
    {
        originTile = Point.Zero;
        if (loc == null || !loc.IsOutdoors || loc is FarmHouse || loc is Cabin || loc.IsRainingHere())
        {
            return false;
        }

        Point? spot = TryFindPicnicSpot(loc, npc.TilePoint);
        if (spot == null)
        {
            spot = TryFindPicnicSpot(loc, owner.TilePoint);
        }

        if (spot == null)
        {
            return false;
        }

        originTile = spot.Value;

        // Utilería temática según personalidad del NPC
        string arch = NpcPersonalityDialogueService.GetPersonalityArchetype(npc);
        string personalityProp = arch switch
        {
            "poetic" or "cozy" => "(O)402", // Sweet Pea
            "technical" => "(O)80",         // Quartz
            "adventurous" => "(O)78",       // Cave Carrot
            _ => "(O)406"                   // Wild Plum
        };

        string[] props = new[]
        {
            "(O)223",          // Galleta
            "(O)395",          // Café
            "(O)613",          // Manzana
            personalityProp
        };

        var session = new ActivePicnicSession
        {
            LocationName = loc.NameOrUniqueName,
            OriginTile = originTile,
            NpcName = npc.Name,
            OwnerFarmerId = owner.UniqueMultiplayerID,
            PropItemIds = props,
            StartedTick = ModEntry.CurrentTick,
            LastChatterTick = ModEntry.CurrentTick,
            SnackGiven = false
        };

        _activeSessions[npc.Name] = session;

        // Sincronizar con peers en Co-op
        ModEntry.MultiplayerSync?.BroadcastPicnicScene(new PicnicSceneSyncMessage
        {
            Active = true,
            LocationName = session.LocationName,
            OriginTileX = session.OriginTile.X,
            OriginTileY = session.OriginTile.Y,
            NpcName = session.NpcName,
            OwnerFarmerId = session.OwnerFarmerId,
            PropItemIds = session.PropItemIds
        });

        return true;
    }

    /// <summary>
    /// Concluye y recoge el picnic de un NPC específico.
    /// </summary>
    public void EndPicnic(string npcName)
    {
        if (string.IsNullOrWhiteSpace(npcName)) return;

        if (_activeSessions.Remove(npcName))
        {
            ModEntry.MultiplayerSync?.BroadcastPicnicScene(new PicnicSceneSyncMessage
            {
                Active = false,
                NpcName = npcName
            });
        }
    }

    /// <summary>
    /// Aplica mensaje de sincronización recibido en multijugador.
    /// </summary>
    public void ApplyRemoteSync(PicnicSceneSyncMessage msg)
    {
        if (msg == null || string.IsNullOrWhiteSpace(msg.NpcName)) return;

        if (msg.Active)
        {
            _activeSessions[msg.NpcName] = new ActivePicnicSession
            {
                LocationName = msg.LocationName,
                OriginTile = new Point(msg.OriginTileX, msg.OriginTileY),
                NpcName = msg.NpcName,
                OwnerFarmerId = msg.OwnerFarmerId,
                PropItemIds = msg.PropItemIds ?? Array.Empty<string>(),
                StartedTick = ModEntry.CurrentTick,
                LastChatterTick = ModEntry.CurrentTick
            };
        }
        else
        {
            _activeSessions.Remove(msg.NpcName);
        }
    }

    /// <summary>
    /// Limpieza completa al cambiar de día o volver al título.
    /// </summary>
    public void Reset()
    {
        _activeSessions.Clear();
        _dailySnackGivenPairs.Clear();
        _dailyStaminaGrantedPairs.Clear();
        _blanketTexture?.Dispose();
        _blanketTexture = null;
    }

    /// <summary>
    /// Dibuja las mantas de picnic y utilería activa en la ubicación actual (llamado en RenderSteps.World_Background).
    /// </summary>
    public void DrawActivePicnics(SpriteBatch b, GameLocation? loc)
    {
        if (b == null || loc == null || _activeSessions.Count == 0) return;

        string currentLocName = loc.NameOrUniqueName;
        Texture2D blanketTex = GetOrCreateBlanketTexture(Game1.graphics.GraphicsDevice);

        foreach (var session in _activeSessions.Values)
        {
            if (!string.Equals(session.LocationName, currentLocName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Vector2 worldPos = new Vector2(session.OriginTile.X * 64f, session.OriginTile.Y * 64f);
            Vector2 screenPos = Game1.GlobalToLocal(Game1.viewport, worldPos);

            // 1. Manta de picnic a cuadros (48x32 px escalada x4 = 192x128 px = 3x2 tiles)
            b.Draw(blanketTex, screenPos, null, Color.White, 0f, Vector2.Zero, 4.0f, SpriteEffects.None, 0.0001f);

            // 2. Utilería sobre la manta (snack, bebida, fruta, ítem temático)
            if (session.PropItemIds != null && session.PropItemIds.Length > 0)
            {
                Vector2[] offsets = new[]
                {
                    new Vector2(36f, 48f),
                    new Vector2(92f, 40f),
                    new Vector2(136f, 52f),
                    new Vector2(76f, 74f)
                };

                for (int i = 0; i < session.PropItemIds.Length && i < offsets.Length; i++)
                {
                    string propId = session.PropItemIds[i];
                    if (string.IsNullOrWhiteSpace(propId)) continue;

                    var data = ItemRegistry.GetDataOrErrorItem(propId);
                    if (data == null) continue;

                    Texture2D tex = data.GetTexture();
                    Rectangle src = data.GetSourceRect();

                    Vector2 propWorldPos = worldPos + offsets[i];
                    Vector2 propScreenPos = Game1.GlobalToLocal(Game1.viewport, propWorldPos);

                    b.Draw(tex, propScreenPos, src, Color.White, 0f, Vector2.Zero, 2.6f, SpriteEffects.None, 0.0002f);
                }
            }
        }
    }

    /// <summary>
    /// Busca un rectángulo 3x2 transitable y despejado en un radio de 1 a 4 casillas.
    /// </summary>
    public Point? TryFindPicnicSpot(GameLocation loc, Point center)
    {
        if (loc == null || !loc.IsOutdoors || loc is FarmHouse || loc is Cabin) return null;
        if (loc.IsRainingHere()) return null;

        for (int r = 1; r <= 4; r++)
        {
            for (int dx = -r; dx <= r; dx++)
            {
                for (int dy = -r; dy <= r; dy++)
                {
                    if (Math.Abs(dx) != r && Math.Abs(dy) != r) continue;
                    Point origin = new Point(center.X + dx, center.Y + dy);
                    if (Is3x2AreaClear(loc, origin))
                    {
                        return origin;
                    }
                }
            }
        }
        return null;
    }

    public void SyncActivePicnicsToPeer(long peerId)
    {
        if (!StardewModdingAPI.Context.IsMainPlayer || _activeSessions.Count == 0) return;

        foreach (var session in _activeSessions.Values)
        {
            ModEntry.MultiplayerSync?.BroadcastPicnicScene(new PicnicSceneSyncMessage
            {
                Active = true,
                LocationName = session.LocationName,
                OriginTileX = session.OriginTile.X,
                OriginTileY = session.OriginTile.Y,
                NpcName = session.NpcName,
                OwnerFarmerId = session.OwnerFarmerId,
                PropItemIds = session.PropItemIds
            }, peerId);
        }
    }

    private bool Is3x2AreaClear(GameLocation loc, Point origin)
    {
        for (int x = 0; x < 3; x++)
        {
            for (int y = 0; y < 2; y++)
            {
                Point pt = new Point(origin.X + x, origin.Y + y);
                if (!loc.isTileOnMap(pt)) return false;

                var locTile = new xTile.Dimensions.Location(pt.X, pt.Y);
                if (!loc.isTilePassable(locTile, Game1.viewport)) return false;

                Vector2 tileVec = new Vector2(pt.X, pt.Y);
                if (loc.objects.ContainsKey(tileVec)) return false;
                if (loc.terrainFeatures.ContainsKey(tileVec)) return false;
                if (loc.buildings != null && loc.buildings.Any(b => b != null && b.occupiesTile(tileVec))) return false;
                if (loc.resourceClumps != null && loc.resourceClumps.Any(rc => rc != null && rc.occupiesTile(pt.X, pt.Y))) return false;
                if (loc.isCollidingWithWarp(new Rectangle(pt.X * 64 + 8, pt.Y * 64 + 8, 48, 48), null) != null) return false;
                if (loc.doesTileHaveProperty(pt.X, pt.Y, "Water", "Back") != null) return false;
                if (loc.isActionableTile(pt.X, pt.Y, Game1.player)) return false;
                if (loc.isTileOccupiedByFarmer(tileVec) != null) return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Genera en memoria la textura pixel-art nítida 48x32 de la manta a cuadros estilo Stardew Valley.
    /// </summary>
    private Texture2D GetOrCreateBlanketTexture(GraphicsDevice device)
    {
        if (_blanketTexture != null && !_blanketTexture.IsDisposed)
        {
            return _blanketTexture;
        }

        const int w = 48;
        const int h = 32;
        var tex = new Texture2D(device, w, h);
        Color[] colors = new Color[w * h];

        Color cWhite = new Color(255, 242, 245, 240);
        Color cPink = new Color(245, 135, 150, 240);
        Color cRedDark = new Color(210, 60, 80, 240);
        Color cBorder = new Color(170, 40, 55, 240);
        Color cCornerFringe = new Color(255, 255, 255, 255);

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int idx = y * w + x;

                // Esquinas anudadas
                bool isCorner = (x == 0 && y == 0) || (x == w - 1 && y == 0) || (x == 0 && y == h - 1) || (x == w - 1 && y == h - 1);
                if (isCorner)
                {
                    colors[idx] = cCornerFringe;
                    continue;
                }

                // Borde cosido exterior
                bool isEdge = (x == 0 || x == w - 1 || y == 0 || y == h - 1);
                if (isEdge)
                {
                    colors[idx] = cBorder;
                    continue;
                }

                // Costura interior con puntos de acento
                bool isSeam = (x == 1 || x == w - 2 || y == 1 || y == h - 2);
                if (isSeam)
                {
                    colors[idx] = ((x + y) % 2 == 0) ? new Color(255, 255, 255, 220) : cBorder;
                    continue;
                }

                // Patrón a cuadros gingham (bloques de 8x8 píxeles)
                int sqX = (x - 2) / 8;
                int sqY = (y - 2) / 8;

                if (sqX % 2 == 0 && sqY % 2 == 0)
                {
                    colors[idx] = cWhite;
                }
                else if (sqX % 2 == 1 && sqY % 2 == 1)
                {
                    colors[idx] = cRedDark;
                }
                else
                {
                    colors[idx] = cPink;
                }
            }
        }

        tex.SetData(colors);
        _blanketTexture = tex;
        return tex;
    }
}
