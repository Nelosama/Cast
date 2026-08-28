namespace CastDesktop.Models
{
    public class CastDevice
    {
        public string Name { get; set; } = string.Empty;
        public string ModelName { get; set; } = "Chromecast";
        public string Uuid { get; set; } = string.Empty;
        public string Host { get; set; } = string.Empty;
        public int Port { get; set; } = 8009;
        public bool Is4k { get; set; }
        public string CastType { get; set; } = "cast";

        public override string ToString()
        {
            return $"{Name} ({ModelName}{(Is4k ? " - 4K" : "")})";
        }
    }
}
