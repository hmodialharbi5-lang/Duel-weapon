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
    public override string Description => "Server-side simulated dual wielding.";
    public override Version Version => new(3, 0, 0);

    private readonly Dictionary<int, int> dualWeapon = new();
    private readonly Dictionary<int, int> lastAnimation = new();
    private readonly HashSet<int> secondaryAttack = new();

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
            secondaryAttack.Clear();
        }

        base.Dispose(disposing);
    }

    private void DualCommand(CommandArgs args)
    {
        if (args.Parameters.Count != 1 ||
            !args.Parameters[0].Equals("on", StringComparison.OrdinalIgnoreCase))
        {
            args.Player.SendErrorMessage(
                "Hold the weapon you want as the second weapon, then use /dual on.");
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

        args.Player.SendSuccessMessage(
            $"Dual wield enabled: {held.Name}. Your normal weapon stays in your hand and a second attack will be simulated.");
    }

    private void DualOffCommand(CommandArgs args)
    {
        dualWeapon.Remove(args.Player.Index);
        lastAnimation.Remove(args.Player.Index);
        secondaryAttack.Remove(args.Player.Index);

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

            if (index < 0 || index >= TShock.Players.Length)
                continue;

            TSPlayer tsPlayer = TShock.Players[index];

            if (tsPlayer == null || !tsPlayer.Active)
                continue;

            Player player = tsPlayer.TPlayer;

            // Detect the beginning of each normal weapon swing.
            int currentAnimation = player.itemAnimation;
            int previousAnimation = lastAnimation.TryGetValue(index, out int old)
                ? old
                : 0;

            lastAnimation[index] = currentAnimation;

            if (currentAnimation <= 0 || previousAnimation > 0)
                continue;

            if (secondaryAttack.Contains(index))
                continue;

            PerformSecondaryAttack(player, pair.Value, index);
        }
    }

    private void PerformSecondaryAttack(Player player, int itemType, int index)
    {
        if (itemType <= 0 || itemType >= Terraria.ID.ItemID.Count)
            return;

        int slot = player.selectedItem;

        if (slot < 0 || slot >= player.inventory.Length)
            return;

        Item primary = player.inventory[slot];

        if (primary == null || primary.IsAir)
            return;

        Item secondary = new Item();
        secondary.SetDefaults(itemType);

        if (secondary.IsAir)
            return;

        // ItemCheck uses the currently selected inventory item.
        // Temporarily put the saved dual weapon there, make the player
        // appear ready for a fresh use, and let Terraria execute the
        // weapon's normal server-side attack code.
        int oldItemAnimation = player.itemAnimation;
        int oldItemTime = player.itemTime;
        int oldItemAnimationMax = player.itemAnimationMax;
        int oldReuseDelay = player.reuseDelay;
        bool oldControlUseItem = player.controlUseItem;
        bool oldReleaseUseItem = player.releaseUseItem;

        secondaryAttack.Add(index);

        try
        {
            player.inventory[slot] = secondary;

            player.itemAnimation = 0;
            player.itemTime = 0;
            player.itemAnimationMax = 0;
            player.reuseDelay = 0;
            player.controlUseItem = true;
            player.releaseUseItem = false;

            player.ItemCheck(index);
        }
        finally
        {
            player.inventory[slot] = primary;

            player.itemAnimation = oldItemAnimation;
            player.itemTime = oldItemTime;
            player.itemAnimationMax = oldItemAnimationMax;
            player.reuseDelay = oldReuseDelay;
            player.controlUseItem = oldControlUseItem;
            player.releaseUseItem = oldReleaseUseItem;

            secondaryAttack.Remove(index);
        }
    }
}
