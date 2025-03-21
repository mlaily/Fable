// Copyright (c) Microsoft Corporation.  All Rights Reserved.  See License.txt in the project root for license information.

/// Contains logic to coordinate assembly resolution and manage the TcImports table of referenced
/// assemblies.
module internal FSharp.Compiler.CompilerImports

open System
open Internal.Utilities.Library
open FSharp.Compiler
open FSharp.Compiler.AbstractIL.IL
open FSharp.Compiler.CheckBasics
open FSharp.Compiler.CompilerConfig
#if !FABLE_COMPILER
open FSharp.Compiler.DependencyManager
#endif
open FSharp.Compiler.DiagnosticsLogger
open FSharp.Compiler.Optimizer
open FSharp.Compiler.TypedTree
open FSharp.Compiler.TypedTreeOps
open FSharp.Compiler.TcGlobals
open FSharp.Compiler.BuildGraph
open FSharp.Compiler.IO
open FSharp.Compiler.Text
open FSharp.Core.CompilerServices

#if !NO_TYPEPROVIDERS
open FSharp.Compiler.TypeProviders
#endif

/// This exception is an old-style way of reporting a diagnostic
exception AssemblyNotResolved of originalName: string * range: range

/// This exception is an old-style way of reporting a diagnostic
exception MSBuildReferenceResolutionWarning of message: string * warningCode: string * range: range

/// This exception is an old-style way of reporting a diagnostic
exception MSBuildReferenceResolutionError of message: string * warningCode: string * range: range

/// Determine if an IL resource attached to an F# assembly is an F# signature data resource
val IsSignatureDataResource: ILResource -> bool

/// Determine if an IL resource attached to an F# assembly is an F# signature data resource (data stream B)
val IsSignatureDataResourceB: ILResource -> bool

/// Determine if an IL resource attached to an F# assembly is an F# optimization data resource
val IsOptimizationDataResource: ILResource -> bool

/// Determine if an IL resource attached to an F# assembly is an F# optimization data resource (data stream B)
val IsOptimizationDataResourceB: ILResource -> bool

/// Determine if an IL resource attached to an F# assembly is an F# quotation data resource for reflected definitions
val IsReflectedDefinitionsResource: ILResource -> bool

val GetResourceNameAndSignatureDataFuncs:
    ILResource list -> (string * ((unit -> ReadOnlyByteMemory) * (unit -> ReadOnlyByteMemory) option)) list

val GetResourceNameAndOptimizationDataFuncs:
    ILResource list -> (string * ((unit -> ReadOnlyByteMemory) * (unit -> ReadOnlyByteMemory) option)) list

#if !FABLE_COMPILER

/// Encode the F# interface data into a set of IL attributes and resources
val EncodeSignatureData:
    tcConfig: TcConfig *
    tcGlobals: TcGlobals *
    exportRemapping: Remap *
    generatedCcu: CcuThunk *
    outfile: string *
    isIncrementalBuild: bool ->
        ILAttribute list * ILResource list

val EncodeOptimizationData:
    tcGlobals: TcGlobals *
    tcConfig: TcConfig *
    outfile: string *
    exportRemapping: Remap *
    (CcuThunk * #CcuOptimizationInfo) *
    isIncrementalBuild: bool ->
        ILResource list

#endif //!FABLE_COMPILER

[<RequireQualifiedAccess>]
type ResolveAssemblyReferenceMode =
    | Speculative
    | ReportErrors

type AssemblyResolution =
    {
        /// The original reference to the assembly.
        originalReference: AssemblyReference

        /// Path to the resolvedFile
        resolvedPath: string

        /// Create the tooltip text for the assembly reference
        prepareToolTip: unit -> string

        /// Whether or not this is an installed system assembly (for example, System.dll)
        sysdir: bool

        /// Lazily populated ilAssemblyRef for this reference.
        mutable ilAssemblyRef: ILAssemblyRef option
    }

#if !NO_TYPEPROVIDERS
type ResolvedExtensionReference =
    | ResolvedExtensionReference of string * AssemblyReference list * Tainted<ITypeProvider> list
#endif

/// Represents a resolved imported binary
[<RequireQualifiedAccess>]
type ImportedBinary =
    { FileName: string
      RawMetadata: IRawFSharpAssemblyData
#if !NO_TYPEPROVIDERS
      ProviderGeneratedAssembly: System.Reflection.Assembly option
      IsProviderGenerated: bool
      ProviderGeneratedStaticLinkMap: ProvidedAssemblyStaticLinkingMap option
#endif
      ILAssemblyRefs: ILAssemblyRef list
      ILScopeRef: ILScopeRef }

/// Represents a resolved imported assembly
[<RequireQualifiedAccess>]
type ImportedAssembly =
    { ILScopeRef: ILScopeRef
      FSharpViewOfMetadata: CcuThunk
      AssemblyAutoOpenAttributes: string list
      AssemblyInternalsVisibleToAttributes: string list
#if !NO_TYPEPROVIDERS
      IsProviderGenerated: bool
      mutable TypeProviders: Tainted<ITypeProvider> list
#endif
      FSharpOptimizationData: InterruptibleLazy<LazyModuleInfo option> }

#if FABLE_COMPILER

/// trimmed-down version of TcImports
[<Sealed>] 
type TcImports =
    internal new: unit -> TcImports
    member FindCcu: range * string -> CcuThunk option
    member SetTcGlobals: TcGlobals -> unit
    member GetTcGlobals: unit -> TcGlobals
    member SetCcuMap: Map<string, ImportedAssembly> -> unit
    member GetImportedAssemblies: unit -> ImportedAssembly list
    member GetImportMap: unit -> Import.ImportMap
    member GetCcusExcludingBase: unit -> CcuThunk list

#else //!FABLE_COMPILER

/// Tables of assembly resolutions
[<Sealed>]
type TcAssemblyResolutions =

    member GetAssemblyResolutions: unit -> AssemblyResolution list

    static member SplitNonFoundationalResolutions:
        tcConfig: TcConfig -> AssemblyResolution list * AssemblyResolution list * UnresolvedAssemblyReference list

    static member BuildFromPriorResolutions:
        tcConfig: TcConfig * AssemblyResolution list * UnresolvedAssemblyReference list -> TcAssemblyResolutions

    static member GetAssemblyResolutionInformation:
        tcConfig: TcConfig -> AssemblyResolution list * UnresolvedAssemblyReference list

[<Sealed>]
type RawFSharpAssemblyData =

    new: ilModule: ILModuleDef * ilAssemblyRefs: ILAssemblyRef list -> RawFSharpAssemblyData

    interface IRawFSharpAssemblyData

/// Represents a table of imported assemblies with their resolutions.
/// Is a disposable object, but it is recommended not to explicitly call Dispose unless you absolutely know nothing will be using its contents after the disposal.
/// Otherwise, simply allow the GC to collect this and it will properly call Dispose from the finalizer.
[<Sealed>]
type TcImports =
    interface IDisposable
    //new: TcImports option -> TcImports
    member DllTable: NameMap<ImportedBinary>

    member GetImportedAssemblies: unit -> ImportedAssembly list

    member GetCcusInDeclOrder: unit -> CcuThunk list

    /// This excludes any framework imports (which may be shared between multiple builds)
    member GetCcusExcludingBase: unit -> CcuThunk list

    member FindDllInfo: CompilationThreadToken * range * string -> ImportedBinary

    member TryFindDllInfo: CompilationThreadToken * range * string * lookupOnly: bool -> ImportedBinary option

    member FindCcuFromAssemblyRef: CompilationThreadToken * range * ILAssemblyRef -> CcuResolutionResult

#if !NO_TYPEPROVIDERS
    member ProviderGeneratedTypeRoots: ProviderGeneratedType list
#endif

    member GetImportMap: unit -> Import.ImportMap

    member DependencyProvider: DependencyProvider

    /// Try to resolve a referenced assembly based on TcConfig settings.
    member TryResolveAssemblyReference:
        CompilationThreadToken * AssemblyReference * ResolveAssemblyReferenceMode ->
            OperationResult<AssemblyResolution list>

    /// Resolve a referenced assembly and report an error if the resolution fails.
    member ResolveAssemblyReference:
        CompilationThreadToken * AssemblyReference * ResolveAssemblyReferenceMode -> AssemblyResolution list

    /// Try to find the given assembly reference by simple name.  Used in magic assembly resolution.  Effectively does implicit
    /// unification of assemblies by simple assembly name.
    member TryFindExistingFullyQualifiedPathBySimpleAssemblyName: string -> string option

    /// Try to find the given assembly reference.
    member TryFindExistingFullyQualifiedPathByExactAssemblyRef: ILAssemblyRef -> string option

#if !NO_TYPEPROVIDERS
    /// Try to find a provider-generated assembly
    member TryFindProviderGeneratedAssemblyByName:
        CompilationThreadToken * assemblyName: string -> System.Reflection.Assembly option
#endif
    /// Report unresolved references that also weren't consumed by any type providers.
    member ReportUnresolvedAssemblyReferences: UnresolvedAssemblyReference list -> unit

    member SystemRuntimeContainsType: string -> bool

    member internal Base: TcImports option

    static member BuildFrameworkTcImports:
        TcConfigProvider * AssemblyResolution list * AssemblyResolution list -> Async<TcGlobals * TcImports>

    static member BuildNonFrameworkTcImports:
        TcConfigProvider * TcImports * AssemblyResolution list * UnresolvedAssemblyReference list * DependencyProvider ->
            Async<TcImports>

    static member BuildTcImports:
        tcConfigP: TcConfigProvider * dependencyProvider: DependencyProvider -> Async<TcGlobals * TcImports>

/// Process a group of #r in F# Interactive.
/// Adds the reference to the tcImports and add the ccu to the type checking environment.
val RequireReferences:
    ctok: CompilationThreadToken *
    tcImports: TcImports *
    tcEnv: TcEnv *
    thisAssemblyName: string *
    resolutions: AssemblyResolution list ->
        TcEnv * ImportedAssembly list

#endif //!FABLE_COMPILER
