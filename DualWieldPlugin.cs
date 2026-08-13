using System;
using System.Collections.Generic;
using Terraria;
using TerrariaApi.Server;
using TShockAPI;

namespace DualWieldTShock;

[ApiVersion(2, 1)]
public sealed class DualWieldPlugin : TerrariaPlugin
{
    public override string Name => "Dual Wield";
    public override string Author => "OpenAI";
    public override string Description => "Simulated dual wielding using the currently held weapon.";
    public override Version Version => new(2, 1, 0);

    private readonly Dictionary<int, int> dualWeapon = new();
    private readonly Dictionary<int, int> lastAnimation = new();
    private readonly Dictionary<int, int> cooldown = new();

    public DualWieldPlugin(Main game) : base(game) { }

    public override void Initialize()
    {
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
        }

        base.Dispose(disposing);
    }

    private void DualCommand(CommandArgs args)
    {
        if (args.Parameters.Count != 1 ||
            !args.Parameters[0].Equals("on", StringComparison.OrdinalIgnoreCase))
        {
            args.Player.SendErrorMessage(
                "Hold the weapon you want to duplicate, then use /dual on");
            return;
        }

        var player = args.Player.TPlayer;
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

        args.Player.SendSuccessMessage(
            $"Dual wield enabled: {held.Name} (ID {held.type}).");
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
                cooldown[index] = cd - 1;

            int animation = player.itemAnimation;
            int previousAnimation =
                lastAnimation.TryGetValue(index, out int oldAnimation)
                    ? oldAnimation
                    : 0;

            lastAnimation[index] = animation;

            // Detect the beginning of a normal weapon swing.
            if (animation <= 0 || previousAnimation > 0)
                continue;

            if (cooldown.TryGetValue(index, out cd) && cd > 0)
                continue;

            FireSecondaryWeapon(player, pair.Value, index);
        }
    }

    private void FireSecondaryWeapon(Player player, int itemType, int playerIndex)
    {
        int selected = player.selectedItem;

        if (selected < 0 || selected >= player.inventory.Length)
            return;

        Item originalItem = player.inventory[selected];

        if (originalItem == null || originalItem.IsAir)
            return;

        // Build a fresh copy of the saved weapon.
        Item secondary = new Item();
        secondary.SetDefaults(itemType);

        if (secondary.IsAir || secondary.type <= 0)
            return;

        // ItemCheck(int) expects an inventory slot, not an Item.
        // Temporarily put the secondary weapon in the selected slot,
        // perform the normal Terraria weapon-use routine, then restore
        // the player's real item immediately afterward.
        int oldAnimation = player.itemAnimation;
        int oldAnimationMax = player.itemAnimationMax;
        int oldTime = player.itemTime;
        int oldTimeMax = player.itemTimeMax;
        bool oldControlUse = player.controlUseItem;

        try
        {
            player.inventory[selected] = secondary;

            player.itemAnimation = 0;
            player.itemTime = 0;
            player.controlUseItem = true;

            player.ItemCheck(selected);

            // Keep the real weapon's animation/timing intact.
            player.itemAnimation = oldAnimation;
            player.itemAnimationMax = oldAnimationMax;
            player.itemTime = oldTime;
            player.itemTimeMax = oldTimeMax;
        }
        finally
        {
            player.controlUseItem = oldControlUse;
            player.inventory[selected] = originalItem;
        }

        int useTime = Math.Max(1, secondary.useTime);
        cooldown[playerIndex] = Math.Max(1, useTime);
    }
}
