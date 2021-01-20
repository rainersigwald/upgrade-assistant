﻿using System.Xml.Linq;

namespace AspNetMigrator.ConfigUpdater
{
    public class ConfigFile
    {
        public ConfigFile(string path)
        {
            Path = path ?? throw new System.ArgumentNullException(nameof(path));
            Contents = XDocument.Load(path, LoadOptions.SetLineInfo);
        }

        public string Path { get; set; }

        public XDocument Contents { get; set; }
    }
}