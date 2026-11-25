using System.Runtime.CompilerServices;
using HarmonyLib;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewAI.Companion.Mod.Domain;
using Xunit;

namespace StardewAI.Companion.Mod.Tests;

[Collection("Native tool ambient location")]
public class CompanionWalkingCollisionTests
{
    [Fact]
    public void FractionalFinalStepBesideObstacleUsesTheActualDestinationBody()
    {
        var farmer = (WalkingFarmer)RuntimeHelpers.GetUninitializedObject(typeof(WalkingFarmer));
        var location = new BoundaryLocation();
        location.name.Value = "Farm";
        var gameField = AccessTools.Field(typeof(Game1), "game1");
        var oldGame = gameField.GetValue(null);
        var oldWorld = Game1.netWorldState;
        var game = (Game1)RuntimeHelpers.GetUninitializedObject(typeof(Game1));
        gameField.SetValue(null, game);
        Game1.netWorldState = new(new StardewValley.Network.NetWorldState());
        AccessTools.Field(typeof(Game1), "instanceGameLocation").SetValue(game, location);
        try
        {
            var position = AccessTools.Field(typeof(Character), "position");
            position.SetValue(farmer, Activator.CreateInstance(position.FieldType));
            var currentLocation = AccessTools.Field(typeof(Character), "currentLocationRef");
            currentLocation.SetValue(farmer, Activator.CreateInstance(currentLocation.FieldType, location));
            farmer.movementDirections = new();
            farmer.Position = new(4032.381f, 1152.3811f);
            farmer.FacingDirection = 3;
            var actor = new FarmerMechanicsActor("fractional-walk");
            AccessTools.Field(typeof(FarmerMechanicsActor), "_gameFarmer").SetValue(actor, farmer);

            // The native body fits at x=4040. Translating the already rounded body
            // by a negative subpixel delta incorrectly probes x=4039 inside a wall.
            Assert.True(ActorGuards.TryFinishWalkingStep(actor, new(4032, 1152)));
            Assert.Equal(new Vector2(4032, 1152), actor.PixelPosition);
            actor.MovePixels(-1, 0);
            Assert.Equal(new Vector2(4032, 1152), actor.PixelPosition);
        }
        finally { Game1.netWorldState = oldWorld; gameField.SetValue(null, oldGame); }
    }

    private sealed class WalkingFarmer : Farmer
    {
        public override int FacingDirection { get; set; }
        public override Rectangle GetBoundingBox() => new((int)Position.X + 8, (int)Position.Y + 16, 48, 32);
    }

    private sealed class BoundaryLocation : GameLocation
    {
        public override bool isCollidingPosition(Rectangle position, xTile.Dimensions.Rectangle viewport,
            bool isFarmer, int damagesFarmer, bool glider, Character character, bool pathfinding,
            bool projectile, bool ignoreCharacterRequirement, bool skipCollisionEffects)
            => position.X < 4040;
    }
}
