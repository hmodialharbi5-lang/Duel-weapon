using System;
using System.Collections.Generic;
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
    public override string Description => "Simulated dual wielding using the saved weapon.";
    public override Version Version => new(2, 2, 0);

    private readonly Dictionary<int, int> dualWeapon = new();
    private readonly Dictionary<int, int> lastAnimation = new();
    private readonly Dictionary<int, int> cooldown = new();

    private MethodInfo? itemCheckMethod;

    public DualWieldPlugin(Main game) : base(game) { }

    public override void Initialize()
    {
        // Resolve ItemCheck at runtime so the plugin can compile even when
        // the Terraria reference exposes a different ItemCheck signature.
        itemCheckMethod = typeof(Player).GetMethod(
            "ItemCheck",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types: new[] { typeof(int) },
            modifiers: null);

        Commands.ChatCommands.Add(new Command("dualwield.use", DualCommand, "dual"));
        Commands.ChatCommands.Add(new Command("dualwield.use", DualOffCommand, "dualoff"));
        Commands.ChatCommands.Add(new Command("dualwield.use", DualInfoCommand, "dualinfo"));

        ServerApi.Hooks.GameUpdate.Register(this, OnGameUpdate);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ServerApi.Hooks.GameUpdate.Deregister(this, OnGameUpdate);
            dualWeapon.Clear();
            lastAnimation.Clear();
            cooldown.Clear();
            itemCheckMethod = null;
        }

        base.Dispose(disposing);
    }

    private void DualCommand(CommandArgs args)
    {
        if (args.Parameters.Count != 1 ||
            !args.Parameters[0].Equals("on", StringComparison.OrdinalIgnoreCase))
        {
            args.Player.SendErrorMessage(
                "Hold the weapon you want as the second weapon, then use /dual on");
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

        if (held == null || held.IsAir || held.type <= 0)
        {
            args.Player.SendErrorMessage("You must be holding a weapon first.");
            return;
        }

        dualWeapon[args.Player.Index] = held.type;
        lastAnimation[args.Player.Index] = 0;
        cooldown[args.Player.Index] = 0;

        args.Player.SendSuccessMessage($"Dual weapon enabled: {held.Name}.");
    }

    private void DualOffCommand(CommandArgs args)
    {
        dualWeapon.Remove(args.Player.Index);
        lastAnimation.Remove(args.Player.Index);
        cooldown.Remove(args.Player.Index);
        args.Player.SendSuccessMessage("Dual wield disabled.");
    }

    private void DualInfoCommand(CommandArgs args)
    {
        if (dualWeapon.TryGetValue(args.Player.Index, out int id))
            args.Player.SendInfoMessage($"Dual weapon item ID: {id}");
        else
            args.Player.SendInfoMessage("Dual wield is off.");
    }

    private void OnGameUpdate(EventArgs args)
    {
        foreach (var pair in dualWeapon)
        {
            int index = pair.Key;
            TSPlayer tsPlayer = TShock.Players[index];

            if (tsPlayer == null || !tsPlayer.Active || tsPlayer.Dead)
                continue;

            Player player = tsPlayer.TPlayer;

            if (cooldown.TryGetValue(index, out int cd) && cd > 0)
            {
                cooldown[index] = cd - 1;
            }

            int animation = player.itemAnimation;
            int previous = lastAnimation.TryGetValue(index, out int old) ? old : 0;
            lastAnimation[index] = animation;

            // A transition from 0 to >0 means the normal weapon just started.
            if (animation <= 0 || previous > 0)
                continue;

            if (cooldown.TryGetValue(index, out cd) && cd > 0)
                continue;

            FireSecondaryWeapon(tsPlayer, pair.Value, index);
        }
    }

    private void FireSecondaryWeapon(TSPlayer tsPlayer, int itemType, int playerIndex)
    {
        Player player = tsPlayer.TPlayer;

        if (itemCheckMethod == null)
        {
            tsPlayer.SendErrorMessage(
                "Dual Wield: this TShock/Terraria build does not expose ItemCheck(int).");
            dualWeapon.Remove(playerIndex);
            return;
        }

        int slot = player.selectedItem;

        if (slot < 0 || slot >= player.inventory.Length)
            return;

        Item original = player.inventory[slot];

        if (original == null || original.IsAir)
            return;

        Item secondary = new Item();
        secondary.SetDefaults(itemType);

        if (secondary.IsAir || secondary.type <= 0)
            return;

        int oldAnimation = player.itemAnimation;
        int oldAnimationMax = player.itemAnimationMax;
        int oldTime = player.itemTime;
        int oldTimeMax = player.itemTimeMax;
        bool oldControlUse = player.controlUseItem;

        try
        {
            player.inventory[slot] = secondary;

            // Let Terraria execute the weapon's own attack logic.
            // Reflection avoids the compile-time signature mismatch seen
            // with some TShock 6.1 reference assemblies.
            player.controlUseItem = true;
            player.itemAnimation = 0;
            player.itemTime = 0;

            itemCheckMethod.Invoke(player, new object[] { slot });
        }
        catch (Exception ex)
        {
            tsPlayer.SendErrorMessage(
                $"Dual Wield attack failed: {ex.InnerException?.Message ?? ex.Message}");
        }
        finally
        {
            player.inventory[slot] = original;
            player.controlUseItem = oldControlUse;
            player.itemAnimation = oldAnimation;
            player.itemAnimationMax = oldAnimationMax;
            player.itemTime = oldTime;
            player.itemTimeMax = oldTimeMax;
        }

        cooldown[playerIndex] = Math.Max(1, secondary.useTime);
    }
}
