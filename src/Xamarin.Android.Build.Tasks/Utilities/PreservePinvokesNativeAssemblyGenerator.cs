using System;
using System.Collections.Generic;
using System.IO;

using Microsoft.Android.Build.Tasks;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

using Xamarin.Android.Tasks.LLVMIR;
using Xamarin.Android.Tools;

namespace Xamarin.Android.Tasks;

class PreservePinvokesNativeAssemblyGenerator : LlvmIrComposer
{
	sealed class PInvoke
	{
		public readonly LlvmIrFunction NativeFunction;
		public readonly PinvokeScanner.PinvokeEntryInfo Info;
		public readonly ulong Hash;

		public PInvoke (LlvmIrModule module, PinvokeScanner.PinvokeEntryInfo pinfo, bool is64Bit)
		{
			Info = pinfo;
			Hash = MonoAndroidHelper.GetXxHash (pinfo.EntryName, is64Bit);

			// All the p/invoke functions use the same dummy signature.  The only thing we care about is
			// a way to reference to the symbol at build time so that we can return pointer to it.  For
			// that all we need is a known name, signature doesn't matter to us.
			var funcSig = new LlvmIrFunctionSignature (name: pinfo.EntryName, returnType: typeof(void));
			NativeFunction = module.DeclareExternalFunction (funcSig);
		}
	}

	sealed class Component
	{
		public readonly string Name;
		public readonly ulong NameHash;
		public readonly List<PInvoke> PInvokes;
		public bool Is64Bit;

		public Component (string name, bool is64Bit)
		{
			Name = name;
			NameHash = MonoAndroidHelper.GetXxHash (name, is64Bit);
			PInvokes = new ();
			Is64Bit = is64Bit;
		}

		public void Add (LlvmIrModule module, PinvokeScanner.PinvokeEntryInfo pinfo)
		{
			PInvokes.Add (new PInvoke (module, pinfo, Is64Bit));
		}

		public void Sort ()
		{
			PInvokes.Sort ((PInvoke a, PInvoke b) => a.Hash.CompareTo (b.Hash));
		}
	}

	// Maps a component name after ridding it of the `lib` prefix and the extension to a "canonical"
	// name of a library, as used in `[DllImport]` attributes.
	readonly Dictionary<string, string> libraryNameMap = new (StringComparer.Ordinal) {
		{ "xa-java-interop",             "java-interop" },
		{ "mono-android.release-static", String.Empty },
		{ "mono-android.release",        String.Empty },
	};

	readonly NativeCodeGenState state;
	readonly ITaskItem[] monoComponents;

	public PreservePinvokesNativeAssemblyGenerator (TaskLoggingHelper log, NativeCodeGenState codeGenState, ITaskItem[] monoComponents)
		: base (log)
	{
		if (codeGenState.PinvokeInfos == null) {
			throw new InvalidOperationException ($"Internal error: {nameof (codeGenState)} `{nameof (codeGenState.PinvokeInfos)}` property is `null`");
		}

		this.state = codeGenState;
		this.monoComponents = monoComponents;
	}

	protected override void Construct (LlvmIrModule module)
	{
		Log.LogDebugMessage ($"[{state.TargetArch}] Constructing p/invoke preserve code");
		List<PinvokeScanner.PinvokeEntryInfo> pinvokeInfos = state.PinvokeInfos!;
		if (pinvokeInfos.Count == 0) {
			// This is a very unlikely scenario, but we will work just fine.  The module that this generator produces will merely result
			// in an empty (but valid) .ll file and an "empty" object file to link into the shared library.
			return;
		}

		Log.LogDebugMessage ("  Looking for enabled native components");
		var componentNames = new List<string> ();
		var nativeComponents = new NativeRuntimeComponents (monoComponents);
		foreach (NativeRuntimeComponents.Archive archiveItem in nativeComponents.KnownArchives) {
			if (!archiveItem.Include) {
				continue;
			}

			Log.LogDebugMessage ($"    {archiveItem.Name}");
			componentNames.Add (archiveItem.Name);
		}

		if (componentNames.Count == 0) {
			Log.LogDebugMessage ("No native framework components are included in the build, not scanning for p/invoke usage");
			return;
		}

		bool is64Bit = state.TargetArch switch {
			AndroidTargetArch.Arm64  => true,
			AndroidTargetArch.X86_64 => true,
			AndroidTargetArch.Arm    => false,
			AndroidTargetArch.X86    => false,
			_                        => throw new NotSupportedException ($"Architecture {state.TargetArch} is not supported here")
		};

		Log.LogDebugMessage ("  Checking discovered p/invokes against the list of components");
		var preservedPerComponent = new Dictionary<string, Component> (StringComparer.OrdinalIgnoreCase);
		var processedCache = new HashSet<string> (StringComparer.OrdinalIgnoreCase);

		foreach (PinvokeScanner.PinvokeEntryInfo pinfo in pinvokeInfos) {
			Log.LogDebugMessage ($"    p/invoke: {pinfo.EntryName} in {pinfo.LibraryName}");
			string key = $"{pinfo.LibraryName}/${pinfo.EntryName}";
			if (processedCache.Contains (key)) {
				Log.LogDebugMessage ($"      already processed");
				continue;
			}

			processedCache.Add (key);
			if (!MustPreserve (pinfo, componentNames)) {
				Log.LogDebugMessage ("      no need to preserve");
				continue;
			}
			Log.LogDebugMessage ("      must be preserved");

			if (!preservedPerComponent.TryGetValue (pinfo.LibraryName, out Component? component)) {
				component = new Component (pinfo.LibraryName, is64Bit);
				preservedPerComponent.Add (component.Name, component);
			}
			component.Add (module, pinfo);
		}

		var components = new List<Component> (preservedPerComponent.Values);
		if (is64Bit) {
			AddFindPinvoke<ulong> (module, components, is64Bit);
		} else {
			AddFindPinvoke<uint> (module, components, is64Bit);
		}
	}

	void AddFindPinvoke<T> (LlvmIrModule module, List<Component> components, bool is64Bit) where T: struct
	{
		var hashType = is64Bit ? typeof (ulong) : typeof (uint);
		var parameters = new List<LlvmIrFunctionParameter> {
			new LlvmIrFunctionParameter (hashType, "library_name_hash") {
				NoUndef = true,
			},

			new LlvmIrFunctionParameter (hashType, "entrypoint_hash") {
				NoUndef = true,
			},

			new LlvmIrFunctionParameter (typeof(IntPtr), "known_library") {
				Align = 1, // it's a reference to C++ `bool`
				Dereferenceable = 1,
				IsCplusPlusReference = true,
				NoCapture = true,
				NonNull = true,
				NoUndef = true,
				WriteOnly = true,
			},
		};

		var sig = new LlvmIrFunctionSignature (
			name: "find_pinvoke",
			returnType: typeof(IntPtr),
			parameters: parameters,
			new LlvmIrFunctionSignature.ReturnTypeAttributes {
				NoUndef = true,
			}
		);

		// TODO: attributes
		var func = new LlvmIrFunction (sig, MakeFindPinvokeAttributeSet (module)) {
			CallingConvention = LlvmIrCallingConvention.Fastcc,
			Linkage = LlvmIrLinkage.Internal,
		};
		module.Add (func);
		func.Body.Add (new LlvmIrFunctionLabelItem ("entry"));

		var libraryNameSwitchEpilog = new LlvmIrFunctionLabelItem ("libNameSW.epilog");
		var componentSwitch = new LlvmIrInstructions.Switch<T> (parameters[0], libraryNameSwitchEpilog, "sw.libname");
		func.Body.Add (componentSwitch);

		components.Sort ((Component a, Component b) => a.NameHash.CompareTo (b.NameHash));
		Log.LogDebugMessage ("  Components to be preserved:");
		foreach (Component component in components) {
			component.Sort ();
			Log.LogDebugMessage ($"    {component.Name} (hash: 0x{component.NameHash:x}; {component.PInvokes.Count} p/invoke(s))");

			LlvmIrFunctionLabelItem componentLabel;
			if (is64Bit) {
				componentLabel = componentSwitch.Add ((T)(object)component.NameHash);
			} else {
				componentLabel = componentSwitch.Add ((T)(object)(uint)component.NameHash);
			}

			func.Body.Add (componentLabel);
			// TODO: output component `switch` here
		}

		func.Body.Add (libraryNameSwitchEpilog);
	}

	LlvmIrFunctionAttributeSet MakeFindPinvokeAttributeSet (LlvmIrModule module)
	{
		var attrSet = new LlvmIrFunctionAttributeSet {
			new MustprogressFunctionAttribute (),
			new NofreeFunctionAttribute (),
			new NorecurseFunctionAttribute (),
			new NosyncFunctionAttribute (),
			new NounwindFunctionAttribute (),
			new WillreturnFunctionAttribute (),
			new MemoryFunctionAttribute {
				Default = MemoryAttributeAccessKind.Write,
				Argmem = MemoryAttributeAccessKind.None,
				InaccessibleMem = MemoryAttributeAccessKind.None,
			},
			new UwtableFunctionAttribute (),
			new NoTrappingMathFunctionAttribute (true),
		};

		return module.AddAttributeSet (attrSet);
	}

	// Returns `true` for all p/invokes that we know are part of our set of components, otherwise returns `false`.
	// Returning `false` merely means that the p/invoke isn't in any of BCL or our code and therefore we shouldn't
	// care.  It doesn't mean the p/invoke will be removed in any way.
	bool MustPreserve (PinvokeScanner.PinvokeEntryInfo pinfo, List<string> components)
	{
		if (String.Compare ("xa-internal-api", pinfo.LibraryName, StringComparison.Ordinal) == 0) {
			return true;
		}

		foreach (string component in components) {
			// The most common pattern for the BCL - file name without extension
			string componentName = Path.GetFileNameWithoutExtension (component);
			if (Matches (pinfo.LibraryName, componentName)) {
				return true;
			}

			// If it starts with `lib`, drop the prefix
			if (componentName.StartsWith ("lib", StringComparison.Ordinal)) {
				if (Matches (pinfo.LibraryName, componentName.Substring (3))) {
					return true;
				}
			}

			// Might require mapping of component name to a canonical one
			if (libraryNameMap.TryGetValue (componentName, out string? mappedComponentName) && !String.IsNullOrEmpty (mappedComponentName)) {
				if (Matches (pinfo.LibraryName, mappedComponentName)) {
					return true;
				}
			}

			// Try full file name, as the last resort
			if (Matches (pinfo.LibraryName, Path.GetFileName (component))) {
				return true;
			}
		}

		return false;

		bool Matches (string libraryName, string componentName)
		{
			return String.Compare (libraryName, componentName, StringComparison.Ordinal) == 0;
		}
	}
}
