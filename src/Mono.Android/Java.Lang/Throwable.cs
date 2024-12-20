using System;
using System.Collections.Generic;

using Java.Interop;

using Android.Runtime;
using System.ComponentModel;
using System.Diagnostics;

namespace Java.Lang {

	public partial class Throwable : JavaException, IJavaObject, IDisposable, IJavaObjectEx
	{
		protected bool is_generated;

		string? nativeStack;

		public Throwable (IntPtr handle, JniHandleOwnership transfer)
			: base (_GetMessage (handle), _GetInnerException (handle))
		{
			if (GetType () == typeof (Throwable))
				is_generated = true;

			// Check if handle was preset by our java activation mechanism
			var peerRef = PeerReference;
			if (peerRef.IsValid) {
				((IJavaPeerable) this).SetJniManagedPeerState (JniManagedPeerStates.Activatable);
				handle          = peerRef.Handle;
				if (peerRef.Type != JniObjectReferenceType.Invalid)
					return;
				transfer        = JniHandleOwnership.DoNotTransfer;
			}

			SetHandle (handle, transfer);
		}

#if JAVA_INTEROP
		static JniMethodInfo?         Throwable_getMessage;
#endif

		static string? _GetMessage (IntPtr handle)
		{
			if (handle == IntPtr.Zero)
				return null;

			IntPtr value;
			const string __id = "getMessage.()Ljava/lang/String;";
			if (Throwable_getMessage == null) {
				Throwable_getMessage = _members.InstanceMethods.GetMethodInfo (__id);
			}
			value = JniEnvironment.InstanceMethods.CallObjectMethod (new JniObjectReference (handle), Throwable_getMessage).Handle;

			return JNIEnv.GetString (value, JniHandleOwnership.TransferLocalRef);
		}

		static JniMethodInfo?         Throwable_getCause;

		static global::System.Exception? _GetInnerException (IntPtr handle)
		{
			if (handle == IntPtr.Zero)
				return null;

			IntPtr value;
			const string __id = "getCause.()Ljava/lang/Throwable;";
			if (Throwable_getCause == null) {
				Throwable_getCause = _members.InstanceMethods.GetMethodInfo (__id);
			}
			value = JniEnvironment.InstanceMethods.CallObjectMethod (new JniObjectReference (handle), Throwable_getCause).Handle;

			var cause = global::Java.Lang.Object.GetObject<Java.Lang.Throwable> (
					value,
					JniHandleOwnership.TransferLocalRef);

			var proxy = cause as JavaProxyThrowable;
			if (proxy != null)
				return proxy.InnerException;

			return cause;
		}

		IntPtr IJavaObjectEx.ToLocalJniHandle ()
		{
			lock (this) {
				var peerRef = PeerReference;
				if (!peerRef.IsValid)
					return IntPtr.Zero;
				return peerRef.NewLocalRef ().Handle;
			}
		}

		public override string StackTrace => base.StackTrace;

		public override string ToString ()
		{
			return base.ToString ();
		}

		[EditorBrowsable (EditorBrowsableState.Never)]
		public int JniIdentityHashCode => base.JniIdentityHashCode;

		[DebuggerBrowsable (DebuggerBrowsableState.Never)]
		[EditorBrowsable (EditorBrowsableState.Never)]
		public JniObjectReference PeerReference => base.PeerReference;

		[DebuggerBrowsable (DebuggerBrowsableState.Never)]
		[EditorBrowsable (EditorBrowsableState.Never)]
		public override JniPeerMembers JniPeerMembers {
			get { return _members; }
		}

		[EditorBrowsable (EditorBrowsableState.Never)]
		public IntPtr Handle {
			get {
				var peerRef = PeerReference;
				if (!peerRef.IsValid)
					return IntPtr.Zero;
				return peerRef.Handle;
			}
		}

		[DebuggerBrowsable (DebuggerBrowsableState.Never)]
		[EditorBrowsable (EditorBrowsableState.Never)]
		protected virtual IntPtr ThresholdClass {
			get { return class_ref; }
		}

		[DebuggerBrowsable (DebuggerBrowsableState.Never)]
		[EditorBrowsable (EditorBrowsableState.Never)]
		protected virtual System.Type ThresholdType {
			get { return typeof (Java.Lang.Throwable); }
		}

		internal IntPtr GetThresholdClass ()
		{
			return ThresholdClass;
		}

		internal System.Type GetThresholdType ()
		{
			return ThresholdType;
		}

		public unsafe Java.Lang.Class? Class {
			[Register ("getClass", "()Ljava/lang/Class;", "GetGetClassHandler")]
			get {
				IntPtr value;
				const string __id = "getClass.()Ljava/lang/Class;";
				value = _members.InstanceMethods.InvokeVirtualObjectMethod (__id, this, null).Handle;
				return global::Java.Lang.Object.GetObject<Java.Lang.Class> (value, JniHandleOwnership.TransferLocalRef);
			}
		}

		[EditorBrowsable (EditorBrowsableState.Never)]
		protected void SetHandle (IntPtr value, JniHandleOwnership transfer)
		{
			var reference = new JniObjectReference (value);
			JNIEnvInit.ValueManager?.ConstructPeer (
					this,
					ref reference,
					value == IntPtr.Zero ? JniObjectReferenceOptions.None : JniObjectReferenceOptions.Copy);
			JNIEnv.DeleteRef (value, transfer);
		}

		public static Throwable FromException (System.Exception e)
		{
			if (e == null)
				throw new ArgumentNullException ("e");

			if (e is Throwable)
				return (Throwable) e;

			return Android.Runtime.JavaProxyThrowable.Create (e);
		}

		public static System.Exception ToException (Throwable e)
		{
			if (e == null)
				throw new ArgumentNullException ("e");

			return e;
		}

		~Throwable ()
		{
		}

		public void UnregisterFromRuntime () => base.UnregisterFromRuntime ();

		public void Dispose () => base.Dispose ();

		protected override void Dispose (bool disposing)
		{
		}
	}
}
