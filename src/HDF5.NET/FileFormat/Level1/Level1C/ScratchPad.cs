﻿using System.IO;

namespace HDF5.NET
{
    public abstract class ScratchPad : FileBlock
    {
        #region Constructors

        public ScratchPad(BinaryReader reader) : base(reader)
        {
            //
        }

        #endregion
    }
}