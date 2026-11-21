using StardewAI.Companion.Mod.Menus;
using Xunit;

public class ChatWireContractTests
{
    [Fact]
    public void ModeButtonShowsActionAndPendingNeverClaimsModeChanged()
    {
        CompanionCommandMenu.AutonomyMode = "command";
        CompanionCommandMenu.ModeChangePending = false;
        Assert.Equal("主动帮忙：关", CompanionCommandMenu.ModeButtonText);
        CompanionCommandMenu.ModeChangePending = true;
        Assert.Equal("保存中", CompanionCommandMenu.ModeButtonText);
        Assert.Equal("command", CompanionCommandMenu.AutonomyMode);
        CompanionCommandMenu.ModeChangePending = false;
        CompanionCommandMenu.AutonomyMode = "free";
        Assert.Equal("主动帮忙：开", CompanionCommandMenu.ModeButtonText);
        CompanionCommandMenu.AutonomyMode = "command";
    }
}
