namespace CS2AutoUpdater
{
    public class Config
    {
        public int UpdateCheckInterval { get; set; }

        public int RestartDelay { get; set; }

        public int ShutdownDelay { get; set; }

        public bool InstantRestartWhenEmpty { get; set; }

        public string? ChatTag { get; set; }
    }
}