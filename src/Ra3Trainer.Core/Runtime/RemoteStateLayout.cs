namespace Ra3Trainer.Core.Runtime;

public static class RemoteStateLayout
{
    // Reserved inside the existing 0x100-byte iEnable allocation; +3C..+FF was unused.
    public const string DispatchLock = "iEnable+40";
    public const string ConversionEpoch = "iEnable+44";
    public const string DispatchEpoch = "iEnable+48";
    public const string CreationFailure = "iEnable+4C";
    public const byte CoordinateConversionDispatch = 0x0E;
    public const string WorldCoordinates = "MustCode+1080";
    public const string ActionDispatch = "iEnable+20";
    public const string ReinforcementUnitId = "iEnable+24";
    public const string ReinforcementCount = "iEnable+28";
    public const string ReinforcementRank = "iEnable+2C";
    public const string MoneyAmount = "iEnable+30";
    public const string PowerValue = "iEnable+34";
    public const string ScPointValue = "iEnable+38";
    public const string SelectedUnitCode = "ra3_1.12.game+8E9838";
}
