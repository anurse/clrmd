﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Runtime.Utilities;
using System.Linq;
using Microsoft.Diagnostics.Runtime.DacInterface;

namespace Microsoft.Diagnostics.Runtime.Desktop
{
    internal class DesktopModule : DesktopBaseModule
    {
        static PdbInfo s_failurePdb = new PdbInfo();
        private readonly bool _reflection;
        private readonly bool _isPE;
        private string _name;
        private readonly string _assemblyName;
        private MetaDataImport _metadata;
        private Dictionary<ClrAppDomain, ulong> _mapping = new Dictionary<ClrAppDomain, ulong>();
        private ulong _address;
        private ulong _imageBase;
        private Lazy<ulong> _size;
        private ulong _metadataStart;
        private ulong _metadataLength;
        private DebuggableAttribute.DebuggingModes? _debugMode;
        private ulong _assemblyAddress;
        private bool _typesLoaded;
        ClrAppDomain[] _appDomainList;
        PdbInfo _pdb;

        public DesktopModule(DesktopRuntimeBase runtime, ulong address, IModuleData data, string name, string assemblyName)
            : base(runtime)
        {
            _address = address;
            Revision = runtime.Revision;
            _imageBase = data.ImageBase;
            _assemblyName = assemblyName;
            _isPE = data.IsPEFile;
            _reflection = data.IsReflection || string.IsNullOrEmpty(name);
            _name = name;
            ModuleId = data.ModuleId;
            ModuleIndex = data.ModuleIndex;
            _metadataStart = data.MetdataStart;
            _metadataLength = data.MetadataLength;
            _assemblyAddress = data.Assembly;
            _size = new Lazy<ulong>(()=>runtime.GetModuleSize(address));

            // This is very expensive in the minidump case, as we may be heading out to the symbol server or
            // reading multiple files from disk. Only optimistically fetch this data if we have full memory.
            if (!runtime.DataReader.IsMinidump && data.LegacyMetaDataImport != IntPtr.Zero)
                _metadata = new MetaDataImport(runtime.DacLibrary, data.LegacyMetaDataImport);
        }

        public override ulong Address
        {
            get
            {
                return _address;
            }
        }

        public override PdbInfo Pdb
        {
            get
            {
                if (_pdb == null)
                {
                    try
                    {
                        using (PEFile pefile = new PEFile(new ReadVirtualStream(_runtime.DataReader, (long)ImageBase, (long)(Size > 0 ? Size : 0x1000)), true))
                        {
                            _pdb = pefile.PdbInfo ?? s_failurePdb;
                        }
                    }
                    catch
                    {
                    }
                }

                return _pdb != s_failurePdb ? _pdb : null;
            }
        }


        internal ulong GetMTForDomain(ClrAppDomain domain, DesktopHeapType type)
        {
            DesktopGCHeap heap = null;
            var mtList = _runtime.GetMethodTableList(_mapping[domain]);

            bool hasToken = type.MetadataToken != 0 && type.MetadataToken != uint.MaxValue;

            uint token = ~0xff000000 & type.MetadataToken;

            foreach (MethodTableTokenPair pair in mtList)
            {
                if (hasToken)
                {
                    if (pair.Token == token)
                        return pair.MethodTable;
                }
                else
                {
                    if (heap == null)
                        heap = (DesktopGCHeap)_runtime.Heap;

                    if (heap.GetTypeByMethodTable(pair.MethodTable, 0) == type)
                        return pair.MethodTable;
                }
            }

            return 0;
        }

        public override IEnumerable<ClrType> EnumerateTypes()
        {
            var heap = (DesktopGCHeap)_runtime.Heap;
            var mtList = _runtime.GetMethodTableList(_address);
            if (_typesLoaded)
            {
                foreach (var type in heap.EnumerateTypes())
                    if (type.Module == this)
                        yield return type;
            }
            else
            {
                if (mtList != null)
                {
                    foreach (var pair in mtList)
                    {
                        ulong mt = pair.MethodTable;
                        if (mt != _runtime.ArrayMethodTable)
                        {
                            // prefetch element type, as this also can load types
                            var type = heap.GetTypeByMethodTable(mt, 0, 0);
                            if (type != null)
                                yield return type;
                        }
                    }
                }

                _typesLoaded = true;
            }
        }

        public override string AssemblyName
        {
            get { return _assemblyName; }
        }

        public override string Name
        {
            get { return _name; }
        }

        public override bool IsDynamic
        {
            get { return _reflection; }
        }

        public override bool IsFile
        {
            get { return _isPE; }
        }

        public override string FileName
        {
            get { return _isPE ? _name : null; }
        }

        internal ulong ModuleIndex { get; private set; }

        internal void AddMapping(ClrAppDomain domain, ulong domainModule)
        {
            DesktopAppDomain appDomain = (DesktopAppDomain)domain;
            _mapping[domain] = domainModule;
        }

        public override IList<ClrAppDomain> AppDomains
        {
            get
            {
                if (_appDomainList == null)
                {
                    _appDomainList = new ClrAppDomain[_mapping.Keys.Count];
                    _appDomainList = _mapping.Keys.ToArray();
                    Array.Sort(_appDomainList, (d, d2) => d.Id.CompareTo(d2.Id));
                }

                return _appDomainList;
            }
        }

        internal override ulong GetDomainModule(ClrAppDomain domain)
        {
            var domains = _runtime.AppDomains;
            if (domain == null)
            {
                foreach (ulong addr in _mapping.Values)
                    return addr;

                return 0;
            }

            if (_mapping.TryGetValue(domain, out ulong value))
                return value;

            return 0;
        }

        internal override MetaDataImport GetMetadataImport()
        {
            if (Revision != _runtime.Revision)
                ClrDiagnosticsException.ThrowRevisionError(Revision, _runtime.Revision);

            if (_metadata != null)
                return _metadata;
            
            _metadata = _runtime.GetMetadataImport(_address);
            return _metadata;
        }

        public override ulong ImageBase
        {
            get { return _imageBase; }
        }


        public override ulong Size
        {
            get
            {
                return _size.Value;
            }
        }


        public override ulong MetadataAddress
        {
            get { return _metadataStart; }
        }

        public override ulong MetadataLength
        {
            get { return _metadataLength; }
        }

        public override object MetadataImport
        {
            get { return GetMetadataImport(); }
        }

        public override DebuggableAttribute.DebuggingModes DebuggingMode
        {
            get
            {
                if (_debugMode == null)
                    InitDebugAttributes();

                Debug.Assert(_debugMode != null);
                return _debugMode.Value;
            }
        }

        private unsafe void InitDebugAttributes()
        {
            MetaDataImport metadata = GetMetadataImport();
            if (metadata == null)
            {
                _debugMode = DebuggableAttribute.DebuggingModes.None;
                return;
            }

            try
            {
                if (metadata.GetCustomAttributeByName(0x20000001, "System.Diagnostics.DebuggableAttribute", out IntPtr data, out uint cbData) && cbData >= 4)
                {
                    byte* b = (byte*)data.ToPointer();
                    ushort opt = b[2];
                    ushort dbg = b[3];

                    _debugMode = (DebuggableAttribute.DebuggingModes)((dbg << 8) | opt);
                }
                else
                {
                    _debugMode = DebuggableAttribute.DebuggingModes.None;
                }
            }
            catch (SEHException)
            {
                _debugMode = DebuggableAttribute.DebuggingModes.None;
            }
        }

        public override ClrType GetTypeByName(string name)
        {
            foreach (ClrType type in EnumerateTypes())
                if (type.Name == name)
                    return type;

            return null;
        }

        public override ulong AssemblyId
        {
            get { return _assemblyAddress; }
        }
    }
}
