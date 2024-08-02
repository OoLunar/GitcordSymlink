using System;

namespace OoLunar.GitcordSymlink.Entities
{
    [Flags]
    public enum GitcordSyncOptions
    {
        None = 0,
        Public = 1 << 0,
        Private = 1 << 1,
        Forked = 1 << 2,
        Archived = 1 << 3,
        All = Public | Private | Forked | Archived
    }
}
