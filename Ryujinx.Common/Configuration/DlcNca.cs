﻿namespace Ryujinx.Common.Configuration
{
    public struct DlcNca
    {
        public string Path { get; set; }
        public ulong TitleId { get; set; }
        public bool Enabled { get; set; }

        public DlcNca(string path, ulong titleId, bool enabled)
        {
            Path = path;
            TitleId = titleId;
            Enabled = enabled;
        }
    }
}