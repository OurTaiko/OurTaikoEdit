namespace OurTaiko
{
    // What Game/PlaySession.cs reads of the game's settings: the range a judge window set by
    // hand is kept to (OurTaikoPlay Core/GameSettings.cs).
    public static class PlaySettings
    {
        public const int JudgeWindowMinMs = 1, JudgeWindowMaxMs = 300;
    }
}
