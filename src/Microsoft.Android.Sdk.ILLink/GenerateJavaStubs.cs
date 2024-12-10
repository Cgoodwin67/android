#nullable enable

using System;
using System.IO;
using System.Linq;
using Java.Interop.Tools.Diagnostics;
using Java.Interop.Tools.JavaCallableWrappers;
using Java.Interop.Tools.JavaCallableWrappers.Adapters;
using Microsoft.Android.Build.Tasks;
using Mono.Cecil;
using Mono.Linker;
using Mono.Linker.Steps;
using Xamarin.Android.Tools;

#if ILLINK
using Resources = Microsoft.Android.Sdk.ILLink.Properties.Resources;
#else
using Resources = Xamarin.Android.Tasks.Properties.Resources;
#endif

namespace Microsoft.Android.Sdk.ILLink;

/// <summary>
/// A trimmer step for generating *.java stubs
/// </summary>
public class GenerateJavaStubs : BaseStep
{
#if ILLINK
	bool IsInitialized => cache is not null;

	void Initialize()
	{
		cache = Context;
		if (Context.TryGetCustomData ("ApplicationJavaClass", out string applicationJavaClass)) {
			ApplicationJavaClass = applicationJavaClass;
		}
	}
#else // !ILLINK
	public GenerateJavaStubs (IMetadataResolver cache) => this.cache = cache;

	readonly
#endif  // !ILLINK
	IMetadataResolver cache;

	static readonly CallableWrapperWriterOptions writer_options = new() {
		CodeGenerationTarget = JavaPeerStyle.XAJavaInterop1
	};

	public string ApplicationJavaClass { get; set; } = "";

	protected override void ProcessAssembly (AssemblyDefinition assembly)
	{
#if ILLINK
		// Call Initialize() on first assembly
		if (!IsInitialized)
			Initialize ();
#endif

		if (!Annotations.HasAction (assembly))
			return;
		var action = Annotations.GetAction (assembly);
		if (action == AssemblyAction.Skip || action == AssemblyAction.Delete)
			return;

		if (assembly.MainModule.HasTypeReference ("Java.Lang.Object"))
				return;

		foreach (var type in assembly.MainModule.Types) {
			ProcessType (assembly, type);
		}
	}

	void ProcessType (AssemblyDefinition assembly, TypeDefinition type)
	{
		if (JavaTypeScanner.ShouldSkipJavaCallableWrapperGeneration (type, cache))
			return;

		// Interfaces are in typemap but they shouldn't have JCW generated for them
		if (type.IsInterface)
			return;

		var reader_options = new CallableWrapperReaderOptions {
			DefaultApplicationJavaClass         = ApplicationJavaClass,
			DefaultGenerateOnCreateOverrides    = false, // this was used only when targetting Android API <= 10, which is no longer supported
			//DefaultMonoRuntimeInitialization    = monoInit,
			//MethodClassifier                    = classifier,
		};

		var generator = CecilImporter.CreateType (type, cache, reader_options);
		using var writer = MemoryStreamPool.Shared.CreateStreamWriter ();

		try {
			generator.Generate (writer, writer_options);

			string path = generator.GetDestinationPath ("TODO");
			Files.CopyIfStreamChanged (writer.BaseStream, path);

			if (generator.HasExport && !assembly.MainModule.AssemblyReferences.Any (r => r.Name == "Mono.Android.Export")) {
				Diagnostic.Error (4210, Resources.XA4210);
			}
		} catch (XamarinAndroidException xae) {
			Diagnostic.Error (xae.Code, xae.MessageWithoutCode, []);
		} catch (DirectoryNotFoundException ex) {
			if (OS.IsWindows) {
				Diagnostic.Error (5301, Resources.XA5301, type.FullName, ex);
			} else {
				Diagnostic.Error (4209, Resources.XA4209, type.FullName, ex);
			}
		} catch (Exception ex) {
			Diagnostic.Error (4209, Resources.XA4209, type.FullName, ex);
		}
	}

	public virtual void LogMessage (string message) => Context.LogMessage (message);
}
