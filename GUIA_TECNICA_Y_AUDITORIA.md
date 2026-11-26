# GUÍA TÉCNICA MAESTRA Y ESPECIFICACIÓN
## Mod: Valley Buddies v3.0.0 (Stardew Valley 1.6+ | SMAPI 4.0+)

**Valley Buddies** (anteriormente AliveNpcsActions) dota a todos los aldeanos del juego de agencia física, navegación humana y vida propia.

### CARPETAS DE ESTE PAQUETE:
- **`Mod_Listo_Para_Instalar/ValleyBuddies/`**:
  Contiene los archivos compilados listos para jugar. Solo se debe copiar la carpeta `ValleyBuddies` dentro de la carpeta `Mods` de Stardew Valley. No requiere el código fuente ni herramientas de compilación.
- **`CodigoFuente_y_Desarrollo/`**:
  Contiene el 100% del código fuente en C#, solución, parches Harmony, motor de navegación y documentación para desarrolladores o auditorías de IA.

### ARQUITECTURA DE 5 PILARES (v3.0):
1. **Escudos Harmony Anti-Extravío (`Bridge/PathfindingSafetyPatches.cs`)**:
   Prefix en `PathFindController.handleWarps` y `NPC.checkSchedule` que previene warps accidentales o saltos a horarios vanilla durante tareas físicas.
2. **Motor de Navegación Humana v3.0 (`Execution/NpcActionExecutor.Pathfinding.cs`)**:
   - `StopNpcCleanly(npc)` elimina el disparo vertical (*moveUp runaway*).
   - Caché en dos capas (estática 60 ticks, dinámica sin caché).
   - Desaceleración a velocidad 2 en interiores y giros ortogonales.
   - Histéresis de seguimiento (distancia <= 2.7 detiene, > 3.3 reanuda).
   - Detección y apertura automática de puertas de valla en radio de 1.5 casillas.
   - Cero teletransportes intra-mapa en las 42 acciones.
3. **Multijugador Real & Sync (`Multiplayer/MultiplayerSyncService.cs`)**:
   - Host como autoridad mundial, reenvío de órdenes de Farmhands con `OwnerPlayerId`.
   - Sincronización de auras de combate, textos flotantes y cartas matutinas.
4. **Vida Hogareña Nocturna (`Execution/NpcActionExecutor.IndoorEvening.cs`)**:
   - A partir de las 18:00 dentro de casas y cabañas, el NPC explora autónomamente chimeneas, libreros, ventanas, cocina, sofá y mascotas.
   - Pijamadas y transición natural al dormir el jugador.
5. **Correo Matutino Dinámico con Café (O)395 (`Systems/DynamicMailService.cs`)**:
   - Invalidación de caché multilingüe `Data/Mail`.
   - Cartas personalizadas con regalo garantizado de Café `(O)395` y puntos de amistad.
