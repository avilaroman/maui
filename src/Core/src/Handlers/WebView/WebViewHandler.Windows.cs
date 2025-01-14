﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace Microsoft.Maui.Handlers
{
	public partial class WebViewHandler : ViewHandler<IWebView, WebView2>
	{
		WebNavigationEvent _eventState;
		readonly HashSet<string> _loadedCookies = new();
		Window? _window;

		protected override WebView2 CreatePlatformView() => new MauiWebView(this);

		internal WebNavigationEvent CurrentNavigationEvent
		{
			get => _eventState;
			set => _eventState = value;
		}

		protected override void ConnectHandler(WebView2 platformView)
		{
			platformView.CoreWebView2Initialized += OnCoreWebView2Initialized;
			base.ConnectHandler(platformView);

			if (platformView.IsLoaded)
				OnLoaded();
			else
				platformView.Loaded += OnWebViewLoaded;
		}

		void OnWebViewLoaded(object sender, RoutedEventArgs e)
		{
			OnLoaded();
		}

		void OnLoaded()
		{
			_window = MauiContext!.GetPlatformWindow();
			_window.Closed += OnWindowClosed;
		}

		private void OnWindowClosed(object sender, WindowEventArgs args)
		{
			Disconnect(PlatformView);
		}

		void Disconnect(WebView2 platformView)
		{
			if (_window is not null)
			{
				_window.Closed -= OnWindowClosed;
				_window = null;
			}

			if (platformView.CoreWebView2 is not null)
			{
				platformView.CoreWebView2.HistoryChanged -= OnHistoryChanged;
				platformView.CoreWebView2.NavigationStarting -= OnNavigationStarting;
				platformView.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
				platformView.CoreWebView2.Stop();
			}

			platformView.Loaded -= OnWebViewLoaded;
			platformView.CoreWebView2Initialized -= OnCoreWebView2Initialized;
			platformView.Close();
		}

		protected override void DisconnectHandler(WebView2 platformView)
		{
			DisconnectHandler(platformView);
			base.DisconnectHandler(platformView);
		}

		void OnCoreWebView2Initialized(WebView2 sender, CoreWebView2InitializedEventArgs args)
		{
			sender.CoreWebView2.HistoryChanged += OnHistoryChanged;
			sender.CoreWebView2.NavigationStarting += OnNavigationStarting;
			sender.CoreWebView2.NavigationCompleted += OnNavigationCompleted;

			sender.UpdateUserAgent(VirtualView);
		}

		void OnHistoryChanged(CoreWebView2 sender, object args)
		{
			PlatformView?.UpdateCanGoBackForward(VirtualView);
		}

		void OnNavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
		{
			if (args.IsSuccess)
				NavigationSucceeded(sender, args);
			else
				NavigationFailed(sender, args);
		}

		void OnNavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
		{
			if (Uri.TryCreate(args.Uri, UriKind.Absolute, out Uri? uri) && uri is not null)
			{
				bool cancel = VirtualView.Navigating(CurrentNavigationEvent, uri.AbsoluteUri);

				args.Cancel = cancel;

				// Reset in this case because this is the last event we will get
				if (cancel)
					_eventState = WebNavigationEvent.NewPage;
			}
		}

		public static void MapSource(IWebViewHandler handler, IWebView webView)
		{
			IWebViewDelegate? webViewDelegate = handler.PlatformView as IWebViewDelegate;

			handler.PlatformView?.UpdateSource(webView, webViewDelegate);
		}

		public static void MapUserAgent(IWebViewHandler handler, IWebView webView)
		{
			handler.PlatformView?.UpdateUserAgent(webView);
		}

		public static void MapGoBack(IWebViewHandler handler, IWebView webView, object? arg)
		{
			if (handler.PlatformView.CanGoBack && handler is WebViewHandler w)
				w.CurrentNavigationEvent = WebNavigationEvent.Back;

			handler.PlatformView?.UpdateGoBack(webView);
		}

		public static void MapGoForward(IWebViewHandler handler, IWebView webView, object? arg)
		{
			if (handler.PlatformView.CanGoForward && handler is WebViewHandler w)
				w.CurrentNavigationEvent = WebNavigationEvent.Forward;

			handler.PlatformView?.UpdateGoForward(webView);
		}

		public static void MapReload(IWebViewHandler handler, IWebView webView, object? arg)
		{
			if (handler is WebViewHandler w)
				w.CurrentNavigationEvent = WebNavigationEvent.Refresh;

			handler.PlatformView?.UpdateReload(webView);
		}

		public static void MapEval(IWebViewHandler handler, IWebView webView, object? arg)
		{
			if (arg is not string script)
				return;

			handler.PlatformView?.Eval(webView, script);
		}

		void NavigationSucceeded(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs e)
		{
			var uri = sender.Source;

			if (uri is not null)
				SendNavigated(uri, CurrentNavigationEvent, WebNavigationResult.Success);

			if (VirtualView is null)
				return;

			PlatformView?.UpdateCanGoBackForward(VirtualView);
		}

		void NavigationFailed(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs e)
		{
			var uri = sender.Source;

			if (!string.IsNullOrEmpty(uri))
				SendNavigated(uri, CurrentNavigationEvent, WebNavigationResult.Failure);
		}

		async void SendNavigated(string url, WebNavigationEvent evnt, WebNavigationResult result)
		{
			if (VirtualView is not null)
			{
				await SyncPlatformCookiesToVirtualView(url);

				VirtualView.Navigated(evnt, url, result);
				PlatformView?.UpdateCanGoBackForward(VirtualView);
			}

			CurrentNavigationEvent = WebNavigationEvent.NewPage;
		}

		async Task SyncPlatformCookiesToVirtualView(string url)
		{
			var myCookieJar = VirtualView.Cookies;

			if (myCookieJar is null)
				return;

			var uri = CreateUriForCookies(url);

			if (uri is null)
				return;

			var cookies = myCookieJar.GetCookies(uri);
			var retrieveCurrentWebCookies = await GetCookiesFromPlatformStore(url);

			var platformCookies = await PlatformView.CoreWebView2.CookieManager.GetCookiesAsync(uri.AbsoluteUri);

			foreach (Cookie cookie in cookies)
			{
				var httpCookie = platformCookies
					.FirstOrDefault(x => x.Name == cookie.Name);

				if (httpCookie is null)
					cookie.Expired = true;
				else
					cookie.Value = httpCookie.Value;
			}

			await SyncPlatformCookies(url);
		}

		internal async Task SyncPlatformCookies(string url)
		{
			var uri = CreateUriForCookies(url);

			if (uri is null)
				return;

			var myCookieJar = VirtualView.Cookies;

			if (myCookieJar is null)
				return;

			await InitialCookiePreloadIfNecessary(url);
			var cookies = myCookieJar.GetCookies(uri);

			if (cookies is null)
				return;

			var retrieveCurrentWebCookies = await GetCookiesFromPlatformStore(url);

			foreach (Cookie cookie in cookies)
			{
				var createdCookie = PlatformView.CoreWebView2.CookieManager.CreateCookie(cookie.Name, cookie.Value, cookie.Domain, cookie.Path);
				PlatformView.CoreWebView2.CookieManager.AddOrUpdateCookie(createdCookie);
			}

			foreach (CoreWebView2Cookie cookie in retrieveCurrentWebCookies)
			{
				if (cookies[cookie.Name] is not null)
					continue;

				PlatformView.CoreWebView2.CookieManager.DeleteCookie(cookie);
			}
		}

		async Task InitialCookiePreloadIfNecessary(string url)
		{
			var myCookieJar = VirtualView.Cookies;

			if (myCookieJar is null)
				return;

			var uri = new Uri(url);

			if (!_loadedCookies.Add(uri.Host))
				return;

			var cookies = myCookieJar.GetCookies(uri);

			if (cookies is not null)
			{
				var existingCookies = await GetCookiesFromPlatformStore(url);

				if (existingCookies.Count == 0)
					return;

				foreach (CoreWebView2Cookie cookie in existingCookies)
				{
					// TODO Ideally we use cookie.ToSystemNetCookie() here, but it's not available for some reason check back later
					if (cookies[cookie.Name] is null)
						myCookieJar.SetCookies(uri,
							new Cookie(cookie.Name, cookie.Value, cookie.Path, cookie.Domain)
							{
								Expires = DateTimeOffset.FromUnixTimeMilliseconds((long)cookie.Expires).DateTime,
								HttpOnly = cookie.IsHttpOnly,
								Secure = cookie.IsSecure,
							}.ToString());
				}
			}
		}

		Task<IReadOnlyList<CoreWebView2Cookie>> GetCookiesFromPlatformStore(string url)
		{
			return PlatformView.CoreWebView2.CookieManager.GetCookiesAsync(url).AsTask();
		}

		Uri? CreateUriForCookies(string url)
		{
			if (url is null)
				return null;

			Uri? uri;

			if (url.Length > 2000)
				url = url.Substring(0, 2000);

			if (Uri.TryCreate(url, UriKind.Absolute, out uri))
			{
				if (string.IsNullOrWhiteSpace(uri.Host))
					return null;

				return uri;
			}

			return null;
		}

		public static void MapEvaluateJavaScriptAsync(IWebViewHandler handler, IWebView webView, object? arg)
		{
			if (arg is EvaluateJavaScriptAsyncRequest request)
			{
				if (handler.PlatformView is null)
				{
					request.SetCanceled();
					return;
				}

				handler.PlatformView.EvaluateJavaScript(request);
			}
		}
	}
}