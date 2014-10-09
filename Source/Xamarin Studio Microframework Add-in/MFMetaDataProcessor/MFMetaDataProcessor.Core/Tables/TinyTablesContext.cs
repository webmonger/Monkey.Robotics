﻿using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;

namespace MFMetaDataProcessor
{
    public sealed class TinyTablesContext
    {
        private readonly HashSet<String> _ignoringAttributes =
            new HashSet<String>(StringComparer.Ordinal)
            {
                // Assembly-level attributes
                "System.Reflection.AssemblyCultureAttribute",
                "System.Reflection.AssemblyVersionAttribute",
                "System.Reflection.AssemblyFileVersionAttribute",
                "System.Reflection.AssemblyTrademarkAttribute",
                "System.Reflection.AssemblyTitleAttribute",
                "System.Reflection.AssemblyProductAttribute",
                "System.Reflection.AssemblyKeyNameAttribute",
                "System.Reflection.AssemblyKeyFileAttribute",
                "System.Reflection.AssemblyInformationalVersionAttribute",
                "System.Reflection.AssemblyFlagsAttribute",
                "System.Reflection.AssemblyDescriptionAttribute",
                "System.Reflection.AssemblyDelaySignAttribute",
                "System.Reflection.AssemblyDefaultAliasAttribute",
                "System.Reflection.AssemblyCopyrightAttribute",
                "System.Reflection.AssemblyConfigurationAttribute",
                "System.Reflection.AssemblyCompanyAttribute",
                "System.Runtime.InteropServices.ComVisibleAttribute",
                "System.Runtime.InteropServices.GuidAttribute",

                // Compiler-specific attributes
                "System.ParamArrayAttribute",
                "System.SerializableAttribute",
                "System.NonSerializedAttribute",
                "System.Runtime.InteropServices.StructLayoutAttribute",
                "System.Runtime.InteropServices.LayoutKind",
                "System.Runtime.InteropServices.OutAttribute",
                "System.Runtime.CompilerServices.ExtensionAttribute",
                "System.Runtime.CompilerServices.MethodImplAttribute",
                "System.Runtime.CompilerServices.InternalsVisibleToAttribute",
                "System.Runtime.CompilerServices.IndexerNameAttribute",
                "System.Runtime.CompilerServices.MethodImplOptions",
                "System.Reflection.FieldNoReflectionAttribute",
                "System.Reflection.DefaultMemberAttribute",

                // Debugger-specific attributes
                "System.Diagnostics.DebuggableAttribute",
                "System.Diagnostics.DebuggerNonUserCodeAttribute",
                "System.Diagnostics.DebuggerStepThroughAttribute",
                "System.Diagnostics.DebuggerDisplayAttribute",
                "System.Diagnostics.DebuggerBrowsableAttribute",
                "System.Diagnostics.DebuggerBrowsableState",
                "System.Diagnostics.DebuggerHiddenAttribute",

                // Compile-time attributes
                "System.AttributeUsageAttribute",
                "System.CLSCompliantAttribute",
                "System.FlagsAttribute",
                "System.ObsoleteAttribute",
                "System.Diagnostics.ConditionalAttribute",

                // Intellisense filtering attributes
                "System.ComponentModel.EditorBrowsableAttribute",

                // Not supported attributes
                "System.MTAThreadAttribute",
                "System.STAThreadAttribute",
                "System.Reflection.DefaultMemberAttribute",

                // VB.NET-specific attributes
                "Microsoft.VisualBasic.ComClassAttribute",
                "Microsoft.VisualBasic.HideModuleNameAttribute",
                "Microsoft.VisualBasic.MyGroupCollectionAttribute",
                "Microsoft.VisualBasic.VBFixedArrayAttribute",
                "Microsoft.VisualBasic.VBFixedStringAttribute",
                "Microsoft.VisualBasic.CompilerServices.DesignerGeneratedAttribute",
                "Microsoft.VisualBasic.CompilerServices.OptionCompareAttribute",
                "Microsoft.VisualBasic.CompilerServices.OptionTextAttribute",
                "Microsoft.VisualBasic.CompilerServices.StandardModuleAttribute",
            };

        public TinyTablesContext(
            AssemblyDefinition assemblyDefinition,
            List<String> explicitTypesOrder,
            ICustomStringSorter stringSorter,
            Boolean applyAttributesCompression)
        {
            AssemblyDefinition = assemblyDefinition;

            foreach (var item in assemblyDefinition.CustomAttributes)
            {
                _ignoringAttributes.Add(item.AttributeType.FullName);
            }

            NativeMethodsCrc = new NativeMethodsCrc(assemblyDefinition);

            var mainModule = AssemblyDefinition.MainModule;

            // External references

            AssemblyReferenceTable = new TinyAssemblyReferenceTable(
                mainModule.AssemblyReferences, this);

            var typeReferences = mainModule.GetTypeReferences()
                .Where(item => !IsAttribute(item))
                .ToList();
            TypeReferencesTable = new TinyTypeReferenceTable(
                typeReferences, this);

            var typeReferencesNames = new HashSet<String>(
                typeReferences.Select(item => item.FullName),
                StringComparer.Ordinal);
            var memberReferences = mainModule.GetMemberReferences()
                .Where(item => typeReferencesNames.Contains(item.DeclaringType.FullName))
                .ToList();
            FieldReferencesTable = new TinyFieldReferenceTable(
                memberReferences.OfType<FieldReference>(), this);
            MethodReferencesTable = new TinyMethodReferenceTable(
                memberReferences.OfType<MethodReference>(), this);

            // Internal types definitions

            var types = GetOrderedTypes(mainModule, explicitTypesOrder);

            TypeDefinitionTable = new TinyTypeDefinitionTable(types, this);
            
            var fields = types
                .SelectMany(item => GetOrderedFields(item.Fields.Where(field => !field.HasConstant)))
                .ToList();
            FieldsTable = new TinyFieldDefinitionTable(fields, this);

            var methods = types.SelectMany(item => GetOrderedMethods(item.Methods)).ToList();

            MethodDefinitionTable = new TinyMethodDefinitionTable(methods, this);

            AttributesTable = new TinyAttributesTable(
                GetAttributes(types, applyAttributesCompression),
                GetAttributes(fields, applyAttributesCompression),
                GetAttributes(methods, applyAttributesCompression),
                this);

            TypeSpecificationsTable = new TinyTypeSpecificationsTable(this);

            // Resources information

            ResourcesTable = new TinyResourcesTable(
                mainModule.Resources, this);
            ResourceDataTable = new TinyResourceDataTable();

            // Strings and signatures

            SignaturesTable = new TinySignaturesTable(this);
            StringTable = new TinyStringTable(stringSorter);

            // Byte code table
            ByteCodeTable = new TinyByteCodeTable(this);

            // Additional information

            ResourceFileTable = new TinyResourceFileTable(this);

            // Pre-allocate strings from some tables
            AssemblyReferenceTable.AllocateStrings();
            TypeReferencesTable.AllocateStrings();
            foreach (var item in memberReferences)
            {
                StringTable.GetOrCreateStringId(item.Name);
                
                var fieldReference = item as FieldReference;
                if (fieldReference != null)
                {
                    SignaturesTable.GetOrCreateSignatureId(fieldReference);
                }

                var methodReference = item as MethodReference;
                if (methodReference != null)
                {
                    SignaturesTable.GetOrCreateSignatureId(methodReference);
                }
            }
        }

        /// <summary>
        /// Gets method reference identifier (external or internal) encoded with appropriate prefix.
        /// </summary>
        /// <param name="methodReference">Method reference in Mono.Cecil format.</param>
        /// <returns>Refernce identifier for passed <paramref name="methodReference"/> value.</returns>
        public UInt16 GetMethodReferenceId(
            MethodReference methodReference)
        {
            UInt16 referenceId;
            if (MethodReferencesTable.TryGetMethodReferenceId(methodReference, out referenceId))
            {
                referenceId |= 0x8000; // External method reference
            }
            else
            {
                MethodDefinitionTable.TryGetMethodReferenceId(methodReference.Resolve(), out referenceId);
            }
            return referenceId;
        }

        public AssemblyDefinition AssemblyDefinition { get; private set; }

        public NativeMethodsCrc NativeMethodsCrc { get; private set; }

        public TinyAssemblyReferenceTable AssemblyReferenceTable { get; private set; }

        public TinyTypeReferenceTable TypeReferencesTable { get; private set; }

        public TinyFieldReferenceTable FieldReferencesTable { get; private set; }

        public TinyMethodReferenceTable MethodReferencesTable { get; private set; }

        public TinyFieldDefinitionTable FieldsTable { get; private set; }

        public TinyMethodDefinitionTable MethodDefinitionTable { get; private set; }

        public TinyTypeDefinitionTable TypeDefinitionTable { get; private set; }

        public TinyAttributesTable AttributesTable { get; private set; }

        public TinyTypeSpecificationsTable TypeSpecificationsTable { get; private set; }

        public TinyResourcesTable ResourcesTable { get; private set; }

        public TinyResourceDataTable ResourceDataTable { get; private set; }

        public TinySignaturesTable SignaturesTable { get; private set; }

        public TinyStringTable StringTable { get; private set; }

        public TinyByteCodeTable ByteCodeTable { get; private set; }

        public TinyResourceFileTable ResourceFileTable { get; private set; }

        private IEnumerable<Tuple<CustomAttribute, UInt16>> GetAttributes(
            IEnumerable<ICustomAttributeProvider> types,
            Boolean applyAttributesCompression)
        {
            if (applyAttributesCompression)
            {
                return types.SelectMany(
                    (item, index) => item.CustomAttributes
                        .Where(attr => !IsAttribute(attr.AttributeType))
                        .OrderByDescending(attr => attr.AttributeType.FullName)
                        .Select(attr => new Tuple<CustomAttribute, UInt16>(attr, (UInt16)index)));
                
            }
            return types.SelectMany(
                (item, index) => item.CustomAttributes
                    .Where(attr => !IsAttribute(attr.AttributeType))
                    .Select(attr => new Tuple<CustomAttribute, UInt16>(attr, (UInt16)index)));
        }

        private Boolean IsAttribute(
            MemberReference typeReference)
        {
            return
                _ignoringAttributes.Contains(typeReference.FullName) ||
                (typeReference.DeclaringType != null &&
                    _ignoringAttributes.Contains(typeReference.DeclaringType.FullName));
        }

        private static List<TypeDefinition> GetOrderedTypes(
            ModuleDefinition mainModule,
            List<String> explicitTypesOrder)
        {
            var unorderedTypes = mainModule.GetTypes()
                .Where(item => item.FullName != "<Module>")
                .ToList();

            if (explicitTypesOrder == null || explicitTypesOrder.Count == 0)
            {
                return SortTypesAccordingUsages(
                    unorderedTypes, mainModule.FullyQualifiedName);
            }

            return explicitTypesOrder
                .Join(unorderedTypes, outer => outer, inner => inner.FullName, (inner, outer) => outer)
                .ToList();
        }

        private static List<TypeDefinition> SortTypesAccordingUsages(
            IEnumerable<TypeDefinition> types,
            String mainModuleName)
        {
            var processedTypes = new HashSet<String>(StringComparer.Ordinal);
            return SortTypesAccordingUsagesImpl(
                types.OrderBy(item => item.FullName),
                mainModuleName, processedTypes)
                .ToList();
        }

        private static IEnumerable<TypeDefinition> SortTypesAccordingUsagesImpl(
            IEnumerable<TypeDefinition> types,
            String mainModuleName,
            ISet<String> processedTypes)
        {
            foreach (var type in types)
            {
                if (processedTypes.Contains(type.FullName))
                {
                    continue;
                }

                if (type.DeclaringType != null)
                {
                    foreach (var declaredIn in SortTypesAccordingUsagesImpl(
                        Enumerable.Repeat(type.DeclaringType, 1), mainModuleName, processedTypes))
                    {
                        yield return declaredIn;
                    }
                }

                foreach (var implement in SortTypesAccordingUsagesImpl(
                    type.Interfaces.Select(itf => itf.Resolve())
                        .Where(item => item.Module.FullyQualifiedName == mainModuleName),
                    mainModuleName, processedTypes))
                {
                    yield return implement;
                }

                if (processedTypes.Add(type.FullName))
                {
                    var operands = type.Methods
                        .Where(item => item.HasBody)
                        .SelectMany(item => item.Body.Instructions)
                        .Select(item => item.Operand)
                        .OfType<MethodReference>()
                        .ToList();

                    foreach (var fieldType in SortTypesAccordingUsagesImpl(
                        operands.SelectMany(GetTypesList)
                            .Where(item => item.Module.FullyQualifiedName == mainModuleName),
                        mainModuleName, processedTypes))
                    {
                        yield return fieldType;
                    }

                    yield return type;
                }
            }
        }

        private static IEnumerable<TypeDefinition> GetTypesList(
            MethodReference methodReference)
        {
            var returnType = methodReference.ReturnType.Resolve();
            if (returnType != null && returnType.FullName != "System.Void")
            {
                yield return returnType;
            }
            foreach (var parameter in methodReference.Parameters)
            {
                var parameterType = parameter.ParameterType.Resolve();
                if (parameterType != null)
                {
                    yield return parameterType;
                }
            }
        }

        private static IEnumerable<MethodDefinition> GetOrderedMethods(
            IEnumerable<MethodDefinition> methods)
        {
            var ordered = methods
                .ToList();

            foreach (var method in ordered.Where(item => item.IsVirtual))
            {
                yield return method;
            }

            foreach (var method in ordered.Where(item => !(item.IsVirtual || item.IsStatic)))
            {
                yield return method;
            }

            foreach (var method in ordered.Where(item => item.IsStatic))
            {
                yield return method;
            }
        }

        private static IEnumerable<FieldDefinition> GetOrderedFields(
            IEnumerable<FieldDefinition> fields)
        {
            var ordered = fields
                .ToList();

            foreach (var method in ordered.Where(item => item.IsStatic))
            {
                yield return method;
            }

            foreach (var method in ordered.Where(item => !item.IsStatic))
            {
                yield return method;
            }
        }
    }
}
