namespace CastDesktop.Models
{
    public class QualityProfile
    {
        public string Name { get; set; } = "Alta";
        public string Resolution { get; set; } = "1920x1080"; // "Native", "3840x2160", "1920x1080", "1280x720"
        public int Framerate { get; set; } = 60; // 30-60
        public int BitrateKbps { get; set; } = 35000; // 15000 - 50000 Mbps
        public string Preset { get; set; } = "slow"; // slow, medium, fast
        public string Codec { get; set; } = "libx264"; // libx264 or libx265
        public string Profile { get; set; } = "high"; // high, main

        public static QualityProfile HighQuality => new QualityProfile
        {
            Name = "Alta (Prioridad Nitidez / Texto)",
            Resolution = "Native (1080p/4K)",
            Framerate = 60,
            BitrateKbps = 35000,
            Preset = "slow",
            Codec = "libx264",
            Profile = "high"
        };

        public static QualityProfile MediumQuality => new QualityProfile
        {
            Name = "Media (Equilibrada)",
            Resolution = "1920x1080",
            Framerate = 30,
            BitrateKbps = 18000,
            Preset = "medium",
            Codec = "libx264",
            Profile = "high"
        };

        public static QualityProfile LowQuality => new QualityProfile
        {
            Name = "Baja (Red Limitada)",
            Resolution = "1280x720",
            Framerate = 30,
            BitrateKbps = 6000,
            Preset = "medium",
            Codec = "libx264",
            Profile = "main"
        };

        public override string ToString()
        {
            return $"{Name} - {Framerate} FPS - {BitrateKbps / 1000} Mbps";
        }
    }
}
