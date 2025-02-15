//
// Unit tests for CFProxy[Settings]
//
// Authors:
//	Sebastien Pouliot <sebastien@xamarin.com>
//
// Copyright 2012 Xamarin Inc. All rights reserved.
//

using System;
using System.Threading;
using System.IO;
using System.Net;

#if XAMCORE_2_0
using Foundation;
using CoreFoundation;
using ObjCRuntime;
#else
using MonoTouch;
using MonoTouch.CoreFoundation;
using MonoTouch.Foundation;
using MonoTouch.ObjCRuntime;
#endif
using NUnit.Framework;

namespace MonoTouchFixtures.CoreFoundation {
	
	[TestFixture]
	[Preserve (AllMembers = true)]
	public class ProxyTest {
		
		[Test]
		public void Fields ()
		{
			// documented but symbols are missing
			// this test will fail if Apple decide to include them in the future
			IntPtr lib = Dlfcn.dlopen (Constants.CoreFoundationLibrary, 0);
			try {
				// http://developer.apple.com/library/ios/documentation/CoreFoundation/Reference/CFProxySupport/Reference/reference.html#//apple_ref/doc/c_ref/kCFProxyAutoConfigurationHTTPResponseKey
				Assert.That (Dlfcn.dlsym (lib, "kCFProxyAutoConfigurationHTTPResponseKey"), Is.EqualTo (IntPtr.Zero), "kCFProxyAutoConfigurationHTTPResponseKey");
				// http://developer.apple.com/library/ios/documentation/CoreFoundation/Reference/CFProxySupport/Reference/reference.html#//apple_ref/doc/c_ref/kCFNetworkProxiesProxyAutoConfigJavaScript
				Assert.That (Dlfcn.dlsym (lib, "kCFNetworkProxiesProxyAutoConfigJavaScript"), Is.EqualTo (IntPtr.Zero), "kCFNetworkProxiesProxyAutoConfigJavaScript");
			}
			finally {
				Dlfcn.dlclose (lib);
			}
		}

#if !__WATCHOS__ && !MONOMAC
		HttpListener listener;
		int port;
		Thread listener_thread;
		[TestFixtureSetUp]
		public void Setup ()
		{
			var listening = new ManualResetEvent (false);
			listener_thread = new Thread (() => {
				try {
					listener = new HttpListener ();

					// Try and find an unused port
					int attemptsLeft = 50;
					Random r = new Random ((int) DateTime.Now.Ticks);
					while (attemptsLeft-- > 0) {
						var newPort = r.Next (49152, 65535); // The suggested range for dynamic ports is 49152-65535 (IANA)
						listener.Prefixes.Clear ();
						listener.Prefixes.Add ("http://*:" + newPort + "/");
						try {
							listener.Start ();
							listening.Set ();
							port = newPort;
							break;
						} catch (Exception ex) {
							Console.WriteLine ($"    Failed to listen on port {newPort}: {ex.Message}");
						}
					}

					try {
						//Console.WriteLine ($"    Test log server listening on: localhost:{port}");
						do {
							var context = listener.GetContext ();
							var request = context.Request;
							var pacPath = Path.Combine (NSBundle.MainBundle.BundlePath, request.RawUrl.Substring (1));
							Console.WriteLine ($"    Serving {pacPath}");
							var buf = File.ReadAllBytes (pacPath);
							context.Response.ContentLength64 = buf.Length;
							context.Response.OutputStream.Write (buf, 0, buf.Length);
							context.Response.OutputStream.Close ();
							context.Response.Close ();
						} while (true);
					} catch (Exception e) {
						if (e is HttpListenerException hle && ((uint) hle.HResult) == 0x80004005) {
							// Console.WriteLine ($"    Listener closed successfully");
						} else {
							Console.WriteLine ($"    Exception during request processing: {e}");
						}
					}
				} catch (Exception e) {
					Console.WriteLine (e);
				}
				// Console.WriteLine ("    Listener thread completed");
			});
			listener_thread.IsBackground = true;
			listener_thread.Start ();
			listening.WaitOne ();
		}

		[TestFixtureTearDown]
		public void TearDown ()
		{
			listener.Stop ();
			listener_thread.Join (TimeSpan.FromSeconds (1));
			listener = null;
			listener_thread = null;
		}

		[Test]
		public void TestPACParsingScript ()
		{
			// get the path for the pac file, try to parse it and ensure that 
			// our cb was called
			string pacPath = Path.Combine (NSBundle.MainBundle.BundlePath, "example.pac");
			NSError error = null;
			var script = File.ReadAllText (pacPath);
			var targetUri = new Uri ("http://docs.xamarin.com");
			var proxies = CFNetwork.ExecuteProxyAutoConfigurationScript (script, targetUri, out error);
			Assert.IsNull (error, "Null error");
			Assert.AreEqual (1, proxies.Length, "Length");
			// assert the data of the proxy, although we are really testing the js used
			Assert.AreEqual (8080, proxies [0].Port, "Port");
		}

		[Test]
		public void TestPACParsingScriptNoProxy ()
		{
			string pacPath = Path.Combine (NSBundle.MainBundle.BundlePath, "example.pac");
			NSError error = null;
			var script = File.ReadAllText (pacPath);
			var targetUri = new Uri ("http://google.com");
			var proxies = CFNetwork.ExecuteProxyAutoConfigurationScript (script, targetUri, out error);
			Assert.IsNull (error, "Null error");
			Assert.IsNotNull (proxies, "Not null proxies");
			Assert.AreEqual (1, proxies.Length, "Proxies length");
			Assert.AreEqual (CFProxyType.None, proxies [0].ProxyType);
		}

		[Test]
		public void TestPACParsingScriptError ()
		{
			NSError error = null;
			var script = "Not VALID js";
			var targetUri = new Uri ("http://google.com");
			var proxies = CFNetwork.ExecuteProxyAutoConfigurationScript (script, targetUri, out error);
			Assert.IsNotNull (error, "Not null error");
			Assert.IsNull (proxies, "Null proxies");
		}

		[Test]
		public void TestPACParsingAsync ()
		{
			CFProxy [] proxies = null;
			NSError error = null;
			NSObject cbClient = null;
			bool done = false;
			string pacPath = Path.Combine (NSBundle.MainBundle.BundlePath, "example.pac");

			var script = File.ReadAllText (pacPath);
			var targetUri = new Uri ("http://docs.xamarin.com");
			
			Exception ex;
			bool foundProxies;
			// similar to the other tests, but we want to ensure that the async/await API works
			TestRuntime.RunAsync (DateTime.Now.AddSeconds (30), async () => {
				try {
					CancellationTokenSource cancelSource = new CancellationTokenSource ();
					CancellationToken cancelToken = cancelSource.Token;
					var result = await CFNetwork.ExecuteProxyAutoConfigurationScriptAsync (script, targetUri, cancelToken);
					proxies = result.proxies;
					error = result.error;
				} catch (Exception e) {
					ex = e;
				} finally {
					done = true;
				}
			}, () => done);
			Assert.IsNull (cbClient, "Null client");
			Assert.IsNull (error, "Null error");
			Assert.IsNotNull (proxies, "Not null proxies");
			Assert.AreEqual (1, proxies.Length, "Length");
			// assert the data of the proxy, although we are really testing the js used
			Assert.AreEqual (8080, proxies [0].Port, "Port");
		}

		[Test]
		public void TestPACParsingAsyncNoProxy ()
		{
			TestRuntime.IgnoreInCI ("CI bots might have proxies setup and will mean that the test will fail when trying to assert they are empty.");

			CFProxy [] proxies = null;
			NSError error = null;
			NSObject cbClient = null;
			bool done = false;
			string pacPath = Path.Combine (NSBundle.MainBundle.BundlePath, "example.pac");

			var script = File.ReadAllText (pacPath);
			var targetUri = new Uri ("http://docs.xamarin.com");

			Exception ex;
			bool foundProxies;
			// similar to the other tests, but we want to ensure that the async/await API works
			TestRuntime.RunAsync (DateTime.Now.AddSeconds (30), async () => {
				try {
					CancellationTokenSource cancelSource = new CancellationTokenSource ();
					CancellationToken cancelToken = cancelSource.Token;
					var result = await CFNetwork.ExecuteProxyAutoConfigurationScriptAsync (script, targetUri, cancelToken);
					proxies = result.proxies;
					error = result.error;
				} catch (Exception e) {
					ex = e;
				} finally {
					done = true;
				}
			}, () => done);
			Assert.IsNull (cbClient, "Null client");
			Assert.IsNull (error, "Null error");
			Assert.IsNotNull (proxies, "Not null proxies");
			Assert.AreEqual (1, proxies.Length, "Proxies length");
			Assert.AreEqual (CFProxyType.None, proxies [0].ProxyType);
		}

		[Test]
		public void TestPACParsingUrl ()
		{
			NSError error;
			var pacUri = new Uri ($"http://localhost:{port}/example.pac");
			var targetUri = new Uri ("http://docs.xamarin.com");
			var proxies = CFNetwork.ExecuteProxyAutoConfigurationUrl (pacUri, targetUri, out error);
			Assert.IsNull (error, "Null error");
			Assert.AreEqual (1, proxies.Length, "Length");
			// assert the data of the proxy, although we are really testing the js used
			Assert.AreEqual (8080, proxies [0].Port, "Port");
		}

		[Test]
		public void TestPacParsingUrlNoProxy ()
		{
			NSError error;
			var pacUri = new Uri ($"http://localhost:{port}/example.pac");
			var targetUri = new Uri ("http://google.com");
			var proxies = CFNetwork.ExecuteProxyAutoConfigurationUrl (pacUri, targetUri, out error);
			Assert.IsNull (error, "Null error");
			Assert.IsNotNull (proxies, "Not null proxies");
			Assert.AreEqual (1, proxies.Length, "Proxies length");
			Assert.AreEqual (CFProxyType.None, proxies [0].ProxyType);
		}

		[Test]
		public void TestPACParsingUrlAsync ()
		{
			CFProxy [] proxies = null;
			NSError error = null;
			NSObject cbClient = null;
			bool done = false;
			var pacUri = new Uri ($"http://localhost:{port}/example.pac");
			var targetUri = new Uri ("http://docs.xamarin.com");

			Exception ex;
			bool foundProxies;
			// similar to the other tests, but we want to ensure that the async/await API works
			TestRuntime.RunAsync (DateTime.Now.AddSeconds (30), async () => {
				try {
					CancellationTokenSource cancelSource = new CancellationTokenSource ();
					CancellationToken cancelToken = cancelSource.Token;
					var result = await CFNetwork.ExecuteProxyAutoConfigurationUrlAsync (pacUri, targetUri, cancelToken);
					proxies = result.proxies;
					error = result.error;
				} catch (Exception e) {
					ex = e;
				} finally {
					done = true;
				}
			}, () => done);
			Assert.IsNull (cbClient, "Null client");
			Assert.IsNull (error, "Null error");
			Assert.IsNotNull (proxies, "Not null proxies");
			Assert.AreEqual (1, proxies.Length, "Length");
			// assert the data of the proxy, although we are really testing the js used
			Assert.AreEqual (8080, proxies [0].Port, "Port");
		}

		[Test]
		public void TestPACParsingUrlAsyncNoProxy ()
		{
			TestRuntime.IgnoreInCI ("CI bots might have proxies setup and will mean that the test will fail when trying to assert they are empty.");
			CFProxy [] proxies = null;
			NSError error = null;
			NSObject cbClient = null;
			bool done = false;
			var pacUri = new Uri ($"http://localhost:{port}/example.pac");
			var targetUri = new Uri ("http://docs.google.com");

			Exception ex;
			bool foundProxies;
			// similar to the other tests, but we want to ensure that the async/await API works
			TestRuntime.RunAsync (DateTime.Now.AddSeconds (30), async () => {
				try {
					CancellationTokenSource cancelSource = new CancellationTokenSource ();
					CancellationToken cancelToken = cancelSource.Token;
					var result = await CFNetwork.ExecuteProxyAutoConfigurationUrlAsync (pacUri, targetUri, cancelToken);
					proxies = result.proxies;
					error = result.error;
				} catch (Exception e) {
					ex = e;
				} finally {
					done = true;
				}
			}, () => done);
			Assert.IsNull (cbClient, "Null client");
			Assert.IsNull (error, "Null error");
			Assert.IsNotNull (proxies, "Not null proxies");
			Assert.AreEqual (1, proxies.Length, "Proxies length");
			Assert.AreEqual (CFProxyType.None, proxies [0].ProxyType);
		}
#endif
	}
}
