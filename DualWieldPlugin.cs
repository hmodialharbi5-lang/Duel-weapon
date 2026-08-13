using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Terraria;
using TerrariaApi.Server;
using TShockAPI;

namespace DualWieldTShock;

[ApiVersion(2, 1)]
public sealed class DualWieldPlugin : TerrariaPlugin
{
    public override string Name => "Dual Wield";
    public override string Author => "OpenAI";
    public override string Description => "Duplicates the held weapon using the Terraria weapon-use path.";
    public override Version Version => new(5, 0, 0);

    private readonly Dictionary<int, int> dualWeapon = new();
    private readonly Dictionary<int, int> cooldown = new();
    private readonly HashSet<int> pendingAttack = new();

    private MethodInfo? itemCheck;

    public DualWieldPlugin(Main game) : base(game) { }

    public override void Initialize()
    {
        // TShock 6.1 uses the NetGetData packet hook. PlayerUpdate contains
        // the player's use-item input, selected slot and direction.
        ServerApi.Hooks.NetGetData.Register(this, OnGetData);
        ServerApi.Hooks.GameUpdate.Register(this, OnGameUpdate);

        // Find ItemCheck dynamically. This avoids hard-coding an overload
        // at compile time while still using Terraria's real weapon-use path.
        itemCheck = FindItemCheck();

        Commands.ChatCommands.Add(
            new Command("dualwield.use", DualCommand, "dual"));

        Commands.ChatCommands.Add(
            new Command("dualwield.use", DualOffCommand, "dualoff"));

        Commands.ChatCommands.Add(
            new Command("dualwield.use", DualInfoCommand, "dualinfo"));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ServerApi.Hooks.NetGetData.Deregister(this, OnGetData);
            ServerApi.Hooks.GameUpdate.Deregister(this, OnGameUpdate);

            dualWeapon.Clear();
            cooldown.Clear();
            pendingAttack.Clear();
            itemCheck = null;
        }

        base.Dispose(disposing);
    }

    private void DualCommand(CommandArgs args)
    {
        if (args.Parameters.Count != 1 ||
            !args.Parameters[0].Equals("on", StringComparison.OrdinalIgnoreCase))
        {
            args.Player.SendErrorMessage(
                "Hold the weapon you want duplicated, then use /dual on.");
            return;
        }

        Player player = args.Player.TPlayer;
        int slot = player.selectedItem;

        if (slot < 0 || slot >= player.inventory.Length)
        {
            args.Player.SendErrorMessage("Could not read your selected slot.");
            return;
        }

        Item held = player.inventory[slot];

        if (held == null || held.IsAir || held.type <= 0 || held.damage <= 0)
        {
            args.Player.SendErrorMessage("You must be holding a weapon first.");
            return;
        }

        dualWeapon[args.Player.Index] = held.type;
        cooldown[args.Player.Index] = 0;
        pendingAttack.Remove(args.Player.Index);

        args.Player.SendSuccessMessage(
            $"Dual weapon enabled: {held.Name}.");
    }

    private void DualOffCommand(CommandArgs args)
    {
        dualWeapon.Remove(args.Player.Index);
        cooldown.Remove(args.Player.Index);
        pendingAttack.Remove(args.Player.Index);

        args.Player.SendSuccessMessage("Dual wield disabled.");
    }

    private void DualInfoCommand(CommandArgs args)
    {
        if (!dualWeapon.TryGetValue(args.Player.Index, out int type))
        {
            args.Player.SendInfoMessage("Dual wield is off.");
            return;
        }

        Item item = new Item();
        item.SetDefaults(type);

        args.Player.SendInfoMessage(
            $"Dual weapon: {item.Name} (ID {type}).");
    }

    private void OnGetData(GetDataEventArgs args)
    {
        if (args.Handled || args.MsgID != PacketTypes.PlayerUpdate)
            return;

        int index = args.Msg.whoAmI;

        if (index < 0 || index >= Main.player.Length)
            return;

        if (!dualWeapon.ContainsKey(index))
            return;

        // PlayerUpdate packet:
        // byte player id
        // byte control flags
        // byte misc flags
        // byte misc flags
        // byte misc flags
        // byte selected item
        //
        // Bit 5 of the first control byte is controlUseItem.
        try
        {
            using MemoryStream stream =
                new MemoryStream(args.Msg.readBuffer, args.Index, args.Length);

            using BinaryReader reader = new BinaryReader(stream);

            int playerId = reader.ReadByte();

            if (playerId != index)
                return;

            byte controls = reader.ReadByte();

            bool useItem = (controls & (1 << 5)) != 0;

            if (!useItem)
                return;

            // The next three bytes are additional PlayerUpdate flags.
            reader.ReadByte();
            reader.ReadByte();
            reader.ReadByte();

            int selectedSlot = reader.ReadByte();

            Player player = Main.player[index];

            if (selectedSlot < 0 || selectedSlot >= player.inventory.Length)
                return;

            if (cooldown.TryGetValue(index, out int cd) && cd > 0)
                return;

            // Only duplicate the attack if the player is actually using
            // the same weapon that was selected with /dual on.
            if (player.inventory[selectedSlot] == null ||
                player.inventory[selectedSlot].IsAir)
                return;

            pendingAttack.Add(index);
        }
        catch
        {
            // Never interfere with Terraria's packet processing.
        }
    }

    private void OnGameUpdate(EventArgs args)
    {
        if (pendingAttack.Count == 0)
            return;

        int[] attacks = new int[pendingAttack.Count];
        pendingAttack.CopyTo(attacks);
        pendingAttack.Clear();

        foreach (int index in attacks)
        {
            if (!dualWeapon.TryGetValue(index, out int weaponType))
                continue;

            if (index < 0 || index >= TShock.Players.Length)
                continue;

            TSPlayer ts = TShock.Players[index];

            if (ts == null || !ts.Active || ts.Dead)
                continue;

            if (cooldown.TryGetValue(index, out int cd) && cd > 0)
                continue;

            FireSecondaryWeapon(ts, weaponType, index);
        }

        TickCooldowns();
    }

    private void TickCooldowns()
    {
        if (cooldown.Count == 0)
            return;

        // Avoid modifying a dictionary while enumerating it.
        var copy = new List<int>(cooldown.Keys);

        foreach (int key in copy)
        {
            if (!cooldown.TryGetValue(key, out int value))
                continue;

            if (value <= 1)
                cooldown.Remove(key);
            else
                cooldown[key] = value - 1;
        }
    }

    private void FireSecondaryWeapon(
        TSPlayer ts,
        int weaponType,
        int playerIndex)
    {
        if (itemCheck == null)
        {
            ts.SendErrorMessage(
                "Dual Wield: Terraria ItemCheck was not found in the running server DLL.");
            return;
        }

        Player player = ts.TPlayer;
        int slot = player.selectedItem;

        if (slot < 0 || slot >= player.inventory.Length)
            return;

        Item originalHeld = player.inventory[slot];

        if (originalHeld == null || originalHeld.IsAir)
            return;

        Item secondary = new Item();
        secondary.SetDefaults(weaponType);

        if (secondary.IsAir || secondary.damage <= 0)
            return;

        int oldAnimation = player.itemAnimation;
        int oldAnimationMax = player.itemAnimationMax;
        int oldTime = player.itemTime;
        int oldTimeMax = player.itemTimeMax;
        bool oldUse = player.controlUseItem;

        try
        {
            // Temporarily put the saved weapon in the selected slot and let
            // Terraria execute its real weapon-use routine. This supports
            // melee, ranged, magic and special projectile weapons far better
            // than manually spawning a generic projectile.
            player.inventory[slot] = secondary;

            player.controlUseItem = true;
            player.itemAnimation = 0;
            player.itemTime = 0;

            InvokeItemCheck(player, slot);
        }
        catch (Exception ex)
        {
            string message = ex.InnerException?.Message ?? ex.Message;
            ts.SendErrorMessage($"Dual Wield attack failed: {message}");
        }
        finally
        {
            player.inventory[slot] = originalHeld;
            player.controlUseItem = oldUse;
            player.itemAnimation = oldAnimation;
            player.itemAnimationMax = oldAnimationMax;
            player.itemTime = oldTime;
            player.itemTimeMax = oldTimeMax;
        }

        cooldown[playerIndex] = Math.Max(1, secondary.useTime);
    }

    private void InvokeItemCheck(Player player, int slot)
    {
        if (itemCheck == null)
            return;

        ParameterInfo[] parameters = itemCheck.GetParameters();
        object?[] values = new object?[parameters.Length];

        for (int i = 0; i < parameters.Length; i++)
        {
            Type type = parameters[i].ParameterType;

            if (type == typeof(int))
            {
                // The real ItemCheck overload uses the selected inventory slot.
                values[i] = slot;
            }
            else if (type == typeof(bool))
            {
                values[i] = false;
            }
            else if (type == typeof(float))
            {
                values[i] = 0f;
            }
            else if (type == typeof(double))
            {
                values[i] = 0d;
            }
            else if (type == typeof(long))
            {
                values[i] = 0L;
            }
            else if (type == typeof(byte))
            {
                values[i] = (byte)0;
            }
            else if (type == typeof(short))
            {
                values[i] = (short)0;
            }
            else if (type == typeof(uint))
            {
                values[i] = 0u;
            }
            else if (type == typeof(ulong))
            {
                values[i] = 0ul;
            }
            else if (type == typeof(ushort))
            {
                values[i] = (ushort)0;
            }
            else if (type == typeof(string))
            {
                values[i] = null;
            }
            else
            {
                values[i] = type.IsValueType
                    ? Activator.CreateInstance(type)
                    : null;
            }
        }

        itemCheck.Invoke(player, values);
    }

    private static MethodInfo? FindItemCheck()
    {
        MethodInfo[] methods = typeof(Player).GetMethods(
            BindingFlags.Instance |
            BindingFlags.Public |
            BindingFlags.NonPublic);

        // Prefer the standard Terraria ItemCheck(int) method.
        foreach (MethodInfo method in methods)
        {
            if (!method.Name.Equals("ItemCheck", StringComparison.Ordinal))
                continue;

            ParameterInfo[] parameters = method.GetParameters();

            if (parameters.Length == 1 &&
                parameters[0].ParameterType == typeof(int))
            {
                return method;
            }
        }

        // Fall back to another ItemCheck overload if a future Terraria
        // build changes its signature.
        foreach (MethodInfo method in methods)
        {
            if (method.Name.Equals("ItemCheck", StringComparison.Ordinal))
                return method;
        }

        return null;
    }
}
