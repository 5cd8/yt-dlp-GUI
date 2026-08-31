namespace YtGui
{
    public sealed class MediaFormat
    {
        public string Id { get; init; } = string.Empty;
        public string Ext { get; init; } = string.Empty;
        public string AudioCodec { get; init; } = string.Empty;
        public string Resolution { get; init; } = string.Empty;
        public string Bitrate { get; init; } = string.Empty;
        public string Samplerate { get; init; } = string.Empty;
        public string Language { get; init; } = string.Empty;
        public bool IsAudioOnly { get; init; }
    }

    public sealed class VideoInfo
    {
        public string Title { get; init; } = string.Empty;
        public string SuggestedFileName { get; init; } = string.Empty;
        public IReadOnlyList<MediaFormat> Formats { get; init; } = Array.Empty<MediaFormat>();
    }
}
