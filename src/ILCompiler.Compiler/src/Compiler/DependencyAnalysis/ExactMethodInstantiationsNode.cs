﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
using System.Diagnostics;
using System.Collections.Generic;

using Internal.Text;
using Internal.TypeSystem;
using Internal.NativeFormat;

namespace ILCompiler.DependencyAnalysis
{
    /// <summary>
    /// Hashtable of all exact (non-canonical) generic method instantiations compiled in the module.
    /// </summary>
    internal sealed class ExactMethodInstantiationsNode : ObjectNode, ISymbolNode
    {
        private ObjectAndOffsetSymbolNode _endSymbol;
        private ExternalReferencesTableNode _externalReferences;

        private HashSet<MethodDesc> _visitedMethods;
        private List<MethodDesc> _exactMethodInstantiationsList;

        public ExactMethodInstantiationsNode(ExternalReferencesTableNode externalReferences)
        {
            _endSymbol = new ObjectAndOffsetSymbolNode(this, 0, "__exact_method_instantiations_End", true);
            _externalReferences = externalReferences;

            _visitedMethods = new HashSet<MethodDesc>();
            _exactMethodInstantiationsList = new List<MethodDesc>();
        }

        public void AppendMangledName(NameMangler nameMangler, Utf8StringBuilder sb)
        {
            sb.Append(nameMangler.CompilationUnitPrefix).Append("__exact_method_instantiations");
        }

        public ISymbolNode EndSymbol => _endSymbol;
        public int Offset => 0;
        public override bool IsShareable => false;
        public override ObjectNodeSection Section => ObjectNodeSection.DataSection;
        public override bool StaticDependenciesAreComputed => true;
        protected override string GetName() => this.GetMangledName();

        public override ObjectData GetData(NodeFactory factory, bool relocsOnly = false)
        {
            // Dependencies for this node are tracked by the method code nodes
            if (relocsOnly)
                return new ObjectData(Array.Empty<byte>(), Array.Empty<Relocation>(), 1, new ISymbolNode[] { this });

            // Zero out the hashset so that we AV if someone tries to insert after we're done.
            _visitedMethods = null;

            // Ensure the native layout data has been saved, in order to get valid Vertex offsets for the signature Vertices
            factory.MetadataManager.NativeLayoutInfo.SaveNativeLayoutInfoWriter(factory);


            NativeWriter nativeWriter = new NativeWriter();
            VertexHashtable hashtable = new VertexHashtable();
            Section nativeSection = nativeWriter.NewSection();
            nativeSection.Place(hashtable);


            foreach (MethodDesc method in _exactMethodInstantiationsList)
            {
                // Get the method pointer vertex

                bool getUnboxingStub = method.OwningType.IsValueType && !method.Signature.IsStatic;
                IMethodNode methodEntryPointNode = factory.MethodEntrypoint(method, getUnboxingStub);
                Vertex methodPointer = nativeWriter.GetUnsignedConstant(_externalReferences.GetIndex(methodEntryPointNode));

                // Get native layout vertices for the declaring type

                ISymbolNode declaringTypeNode = factory.NecessaryTypeSymbol(method.OwningType);
                Vertex declaringType = nativeWriter.GetUnsignedConstant(_externalReferences.GetIndex(declaringTypeNode));

                // Get a vertex sequence for the method instantiation args if any

                VertexSequence arguments = new VertexSequence();
                foreach (var arg in method.Instantiation)
                {
                    ISymbolNode argNode = factory.NecessaryTypeSymbol(arg);
                    arguments.Append(nativeWriter.GetUnsignedConstant(_externalReferences.GetIndex(argNode)));
                }

                // Get the name and sig of the method.
                // Note: the method name and signature are stored in the NativeLayoutInfo blob, not in the hashtable we build here.

                NativeLayoutMethodNameAndSignatureVertexNode nameAndSig = factory.NativeLayout.MethodNameAndSignatureVertex(method.GetTypicalMethodDefinition());
                NativeLayoutPlacedSignatureVertexNode placedNameAndSig = factory.NativeLayout.PlacedSignatureVertex(nameAndSig);
                Debug.Assert(placedNameAndSig.SavedVertex != null);
                Vertex placedNameAndSigOffsetSig = nativeWriter.GetOffsetSignature(placedNameAndSig.SavedVertex);

                // Get the vertex for the completed method signature

                Vertex methodSignature = nativeWriter.GetTuple(declaringType, placedNameAndSigOffsetSig, arguments);

                // Make the generic method entry vertex

                Vertex entry = nativeWriter.GetTuple(methodSignature, methodPointer);

                // Add to the hash table, hashed by the containing type's hashcode
                uint hashCode = (uint)method.OwningType.GetHashCode();
                hashtable.Append(hashCode, nativeSection.Place(entry));
            }

            MemoryStream stream = new MemoryStream();
            nativeWriter.Save(stream);

            byte[] streamBytes = stream.ToArray();

            _endSymbol.SetSymbolOffset(streamBytes.Length);

            return new ObjectData(streamBytes, Array.Empty<Relocation>(), 1, new ISymbolNode[] { this, _endSymbol });
        }

        public static DependencyList GetExactMethodInstantiationDependenciesForMethod(NodeFactory factory, MethodDesc method)
        {
            if (!IsMethodEligibleForTracking(method))
                return null;

            DependencyList dependencies = new DependencyList();

            // Method entry point dependency
            bool getUnboxingStub = method.OwningType.IsValueType && !method.Signature.IsStatic;
            IMethodNode methodEntryPointNode = factory.MethodEntrypoint(method, getUnboxingStub);
            dependencies.Add(new DependencyListEntry(methodEntryPointNode, "Exact method instantiation entry"));

            // Get native layout dependencies for the declaring type
            dependencies.Add(new DependencyListEntry(factory.NecessaryTypeSymbol(method.OwningType), "Exact method instantiation entry"));

            // Get native layout dependencies for the method instantiation args
            foreach (var arg in method.Instantiation)
                dependencies.Add(new DependencyListEntry(factory.NecessaryTypeSymbol(arg), "Exact method instantiation entry"));

            // Get native layout dependencies for the method signature.
            NativeLayoutMethodNameAndSignatureVertexNode nameAndSig = factory.NativeLayout.MethodNameAndSignatureVertex(method.GetTypicalMethodDefinition());
            dependencies.Add(new DependencyListEntry(factory.NativeLayout.PlacedSignatureVertex(nameAndSig), "Exact method instantiation entry"));

            return dependencies;
        }

        public void AddEntryIfEligible(NodeFactory factory, MethodDesc method)
        {
            // Check if we already saw this method
            if (!_visitedMethods.Add(method))
                return;

            if (!IsMethodEligibleForTracking(method))
                return;

            _exactMethodInstantiationsList.Add(method);
        }

        private static bool IsMethodEligibleForTracking(MethodDesc method)
        {
            // Runtime determined methods should never show up here.
            Debug.Assert(!method.IsRuntimeDeterminedExactMethod);

            if (method.IsAbstract)
                return false;

            if (!method.HasInstantiation)
                return false;

            // This hashtable is only for method instantiations that don't use generic dictionaries,
            // so check if the given method is shared before proceeding
            if (method.IsSharedByGenericInstantiations || method.GetCanonMethodTarget(CanonicalFormKind.Specific) != method)
                return false;

            return true;
        }
    }
}