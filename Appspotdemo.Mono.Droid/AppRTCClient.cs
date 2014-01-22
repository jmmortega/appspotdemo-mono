﻿/*
 * libjingle
 * Copyright 2013, Google Inc.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *
 *  1. Redistributions of source code must retain the above copyright notice,
 *     this list of conditions and the following disclaimer.
 *  2. Redistributions in binary form must reproduce the above copyright notice,
 *     this list of conditions and the following disclaimer in the documentation
 *     and/or other materials provided with the distribution.
 *  3. The name of the author may not be used to endorse or promote products
 *     derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE AUTHOR ``AS IS'' AND ANY EXPRESS OR IMPLIED
 * WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF
 * MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO
 * EVENT SHALL THE AUTHOR BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
 * SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO,
 * PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS;
 * OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY,
 * WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR
 * OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF
 * ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */
using System;
using System.Collections.Generic;
using Android.App;
using Android.OS;
using Android.Util;
using Java.IO;
using Java.Net;
using Java.Util;
using Java.Util.Regex;
using Org.Json;
using Org.Webrtc;
using Pattern = Android.OS.Pattern;

namespace Appspotdemo.Mono.Droid
{
	/// <summary>
	/// Negotiates signaling for chatting with apprtc.appspot.com "rooms".
	/// Uses the client<->server specifics of the apprtc AppEngine webapp.
	/// 
	/// To use: create an instance of this object (registering a message handler) and
	/// call connectToRoom().  Once that's done call sendMessage() and wait for the
	/// registered handler to be called with received messages.
	/// </summary>
	public class AppRTCClient
	{
		private const string TAG = "AppRTCClient";
		private GAEChannelClient channelClient;
		private readonly Activity activity;
		private readonly GAEChannelClient.MessageHandler gaeHandler;
		private readonly IceServersObserver iceServersObserver;

		// These members are only read/written under sendQueue's lock.
		private LinkedList<string> sendQueue = new LinkedList<string>();
		private AppRTCSignalingParameters appRTCSignalingParameters;

		/// <summary>
		/// Callback fired once the room's signaling parameters specify the set of
		/// ICE servers to use.
		/// </summary>
		public interface IceServersObserver
		{
			void onIceServers(IList<PeerConnection.IceServer> iceServers);
		}

		public AppRTCClient(Activity activity, GAEChannelClient.MessageHandler gaeHandler, IceServersObserver iceServersObserver)
		{
			this.activity = activity;
			this.gaeHandler = gaeHandler;
			this.iceServersObserver = iceServersObserver;
		}

		/// <summary>
		/// Asynchronously connect to an AppRTC room URL, e.g.
		/// https://apprtc.appspot.com/?r=NNN and register message-handling callbacks
		/// on its GAE Channel.
		/// </summary>
		public virtual void connectToRoom(string url)
		{
			while (url.IndexOf('?') < 0)
			{
				// Keep redirecting until we get a room number.
				(new RedirectResolver(this)).Execute(url);
				return; // RedirectResolver above calls us back with the next URL.
			}
			(new RoomParameterGetter(this)).Execute(url);
		}

		/// <summary>
		/// Disconnect from the GAE Channel.
		/// </summary>
		public virtual void disconnect()
		{
			if (channelClient != null)
			{
				channelClient.close();
				channelClient = null;
			}
		}

		/// <summary>
		/// Queue a message for sending to the room's channel and send it if already
		/// connected (other wise queued messages are drained when the channel is
		///   eventually established).
		/// </summary>
		public virtual void sendMessage(string msg)
		{
			lock (this)
			{
				lock (sendQueue)
				{
					sendQueue.AddLast(msg);
				}
				requestQueueDrainInBackground();
			}
		}

		public virtual bool Initiator
		{
			get
			{
				return appRTCSignalingParameters.initiator;
			}
		}

		public virtual MediaConstraints pcConstraints()
		{
			return appRTCSignalingParameters.pcConstraints;
		}

		public virtual MediaConstraints videoConstraints()
		{
			return appRTCSignalingParameters.videoConstraints;
		}

		// Struct holding the signaling parameters of an AppRTC room.
		private class AppRTCSignalingParameters
		{
			private readonly AppRTCClient outerInstance;

			public readonly IList<PeerConnection.IceServer> iceServers;
			public readonly string gaeBaseHref;
			public readonly string channelToken;
			public readonly string postMessageUrl;
			public readonly bool initiator;
			public readonly MediaConstraints pcConstraints;
			public readonly MediaConstraints videoConstraints;

			public AppRTCSignalingParameters(AppRTCClient outerInstance, IList<PeerConnection.IceServer> iceServers, string gaeBaseHref, string channelToken, string postMessageUrl, bool initiator, MediaConstraints pcConstraints, MediaConstraints videoConstraints)
			{
				this.outerInstance = outerInstance;
				this.iceServers = iceServers;
				this.gaeBaseHref = gaeBaseHref;
				this.channelToken = channelToken;
				this.postMessageUrl = postMessageUrl;
				this.initiator = initiator;
				this.pcConstraints = pcConstraints;
				this.videoConstraints = videoConstraints;
			}
		}

		// Load the given URL and return the value of the Location header of the
		// resulting 302 response.  If the result is not a 302, throws.
		private class RedirectResolver : AsyncTask<string, int, string>
		{
			private readonly AppRTCClient outerInstance;

			public RedirectResolver(AppRTCClient outerInstance)
			{
				this.outerInstance = outerInstance;
			}

			protected override string RunInBackground(params string[] urls)
			{
				if (urls.Length != 1)
				{
					throw new Exception("Must be called with a single URL");
				}
				try
				{
					return followRedirect(urls[0]);
				}
				catch (IOException e)
				{
					throw new Exception("Error", e);
				}
			}

			protected override void OnPostExecute(string url)
			{
				outerInstance.connectToRoom(url);
			}

			internal virtual string followRedirect(string url)
			{
				HttpURLConnection connection = (HttpURLConnection)(new URL(url)).OpenConnection();
				connection.InstanceFollowRedirects = false;
				int code = (int)connection.ResponseCode;
				if (code != (int)HttpURLConnection.HttpMovedTemp)
				{
					throw new IOException("Unexpected response: " + code + " for " + url + ", with contents: " + drainStream(connection.InputStream));
				}
				int n = 0;
				string name, value;
				while ((name = connection.GetHeaderFieldKey(n)) != null)
				{
					value = connection.GetHeaderField(n);
					if (name.Equals("Location"))
					{
						return value;
					}
					++n;
				}
				throw new IOException("Didn't find Location header!");
			}
		}

		// AsyncTask that converts an AppRTC room URL into the set of signaling
		// parameters to use with that room.
		private class RoomParameterGetter : AsyncTask<string, int, AppRTCSignalingParameters>
		{
			private readonly AppRTCClient outerInstance;

			public RoomParameterGetter(AppRTCClient outerInstance)
			{
				this.outerInstance = outerInstance;
			}

			protected override AppRTCSignalingParameters RunInBackground(params string[] urls)
			{
				if (urls.Length != 1)
				{
					throw new Exception("Must be called with a single URL");
				}
				try
				{
					return getParametersForRoomUrl(urls[0]);
				}
				catch (IOException e)
				{
					throw new Exception("Error", e);
				}
			}

			protected override void OnPostExecute(AppRTCSignalingParameters @params)
			{
				outerInstance.channelClient = new GAEChannelClient(outerInstance.activity, @params.channelToken, outerInstance.gaeHandler);
				lock (outerInstance.sendQueue)
				{
					outerInstance.appRTCSignalingParameters = @params;
				}
				outerInstance.requestQueueDrainInBackground();
				outerInstance.iceServersObserver.onIceServers(outerInstance.appRTCSignalingParameters.iceServers);
			}

			// Fetches |url| and fishes the signaling parameters out of the HTML via
			// regular expressions.
			//
			// TODO(fischman): replace this hackery with a dedicated JSON-serving URL in
			// apprtc so that this isn't necessary (here and in other future apps that
			// want to interop with apprtc).
			internal virtual AppRTCSignalingParameters getParametersForRoomUrl(string url)
			{
				Java.Util.Regex.Pattern fullRoomPattern = Java.Util.Regex.Pattern.Compile(".*\n *Sorry, this room is full\\..*");

				string roomHtml = drainStream((new URL(url)).OpenConnection().InputStream);

				Matcher fullRoomMatcher = fullRoomPattern.Matcher(roomHtml);
				if (fullRoomMatcher.Find())
				{
					throw new IOException("Room is full!");
				}

				string gaeBaseHref = url.Substring(0, url.IndexOf('?'));
				string token = getVarValue(roomHtml, "channelToken", true);
				string postMessageUrl = "/message?r=" + getVarValue(roomHtml, "roomKey", true) + "&u=" + getVarValue(roomHtml, "me", true);
				bool initiator = getVarValue(roomHtml, "initiator", false).Equals("1");
				List<PeerConnection.IceServer> iceServers = outerInstance.iceServersFromPCConfigJSON(getVarValue(roomHtml, "pcConfig", false));

				bool isTurnPresent = false;
				foreach (PeerConnection.IceServer server in iceServers)
				{
					if (server.Uri.StartsWith("turn:"))
					{
						isTurnPresent = true;
						break;
					}
				}
				if (!isTurnPresent)
				{
					iceServers.Add(requestTurnServer(getVarValue(roomHtml, "turnUrl", true)));
				}

				MediaConstraints pcConstraints = constraintsFromJSON(getVarValue(roomHtml, "pcConstraints", false));
				Log.Debug(TAG, "pcConstraints: " + pcConstraints);

				MediaConstraints videoConstraints = constraintsFromJSON(getVideoConstraints(getVarValue(roomHtml, "mediaConstraints", false)));

				Log.Debug(TAG, "videoConstraints: " + videoConstraints);

				return new AppRTCSignalingParameters(outerInstance, iceServers, gaeBaseHref, token, postMessageUrl, initiator, pcConstraints, videoConstraints);
			}

			internal virtual string getVideoConstraints(string mediaConstraintsString)
			{
				try
				{
					JSONObject json = new JSONObject(mediaConstraintsString);
					// Tricksy handling of values that are allowed to be (boolean or
					// MediaTrackConstraints) by the getUserMedia() spec.  There are three
					// cases below.
					if (!json.Has("video") || !json.OptBoolean("video", true))
					{
						// Case 1: "video" is not present, or is an explicit "false" boolean.
						return null;
					}
					if (json.OptBoolean("video", false))
					{
						// Case 2: "video" is an explicit "true" boolean.
						return "{\"mandatory\": {}, \"optional\": []}";
					}
					// Case 3: "video" is an object.
					return json.GetJSONObject("video").ToString();
				}
				catch (JSONException e)
				{
					throw new Exception("Error", e);
				}
			}

			internal virtual MediaConstraints constraintsFromJSON(string jsonString)
			{
				if (jsonString == null)
				{
					return null;
				}
				try
				{
					MediaConstraints constraints = new MediaConstraints();
					JSONObject json = new JSONObject(jsonString);
					JSONObject mandatoryJSON = json.OptJSONObject("mandatory");
					if (mandatoryJSON != null)
					{
						JSONArray mandatoryKeys = mandatoryJSON.Names();
						if (mandatoryKeys != null)
						{
							for (int i = 0; i < mandatoryKeys.Length(); ++i)
							{
								string key = mandatoryKeys.GetString(i);
								string value = mandatoryJSON.GetString(key);
								constraints.Mandatory.Add(new MediaConstraints.KeyValuePair(key, value));
							}
						}
					}
					JSONArray optionalJSON = json.OptJSONArray("optional");
					if (optionalJSON != null)
					{
						for (int i = 0; i < optionalJSON.Length(); ++i)
						{
							JSONObject keyValueDict = optionalJSON.GetJSONObject(i);
							string key = keyValueDict.Names().GetString(0);
							string value = keyValueDict.GetString(key);
							constraints.Optional.Add(new MediaConstraints.KeyValuePair(key, value));
						}
					}
					return constraints;
				}
				catch (JSONException e)
				{
					throw new Exception("Error", e);
				}
			}

			// Scan |roomHtml| for declaration & assignment of |varName| and return its
			// value, optionally stripping outside quotes if |stripQuotes| requests it.
			internal virtual string getVarValue(string roomHtml, string varName, bool stripQuotes)
			{
				Java.Util.Regex.Pattern pattern = Java.Util.Regex.Pattern.Compile(".*\n *var " + varName + " = ([^\n]*);\n.*");
				Matcher matcher = pattern.Matcher(roomHtml);
				if (!matcher.Find())
				{
					throw new IOException("Missing " + varName + " in HTML: " + roomHtml);
				}
				string varValue = matcher.Group(1);
				if (matcher.Find())
				{
					throw new IOException("Too many " + varName + " in HTML: " + roomHtml);
				}
				if (stripQuotes)
				{
					varValue = varValue.Substring(1, varValue.Length - 1 - 1);
				}
				return varValue;
			}

			// Requests & returns a TURN ICE Server based on a request URL.  Must be run
			// off the main thread!
			internal virtual PeerConnection.IceServer requestTurnServer(string url)
			{
				try
				{
					URLConnection connection = (new URL(url)).OpenConnection();
					connection.AddRequestProperty("user-agent", "Mozilla/5.0");
					connection.AddRequestProperty("origin", "https://apprtc.appspot.com");
					string response = drainStream(connection.InputStream);
					JSONObject responseJSON = new JSONObject(response);
					string uri = responseJSON.GetJSONArray("uris").GetString(0);
					string username = responseJSON.GetString("username");
					string password = responseJSON.GetString("password");
					return new PeerConnection.IceServer(uri, username, password);
				}
				catch (JSONException e)
				{
					throw new Exception("Error", e);
				}
				catch (IOException e)
				{
					throw new Exception("Error", e);
				}
			}
		}

		// Return the list of ICE servers described by a WebRTCPeerConnection
		// configuration string.
		private List<PeerConnection.IceServer> iceServersFromPCConfigJSON(string pcConfig)
		{
			try
			{
				JSONObject json = new JSONObject(pcConfig);
				JSONArray servers = json.GetJSONArray("iceServers");
				List<PeerConnection.IceServer> ret = new List<PeerConnection.IceServer>();
				for (int i = 0; i < servers.Length(); ++i)
				{
					JSONObject server = servers.GetJSONObject(i);
					string url = server.GetString("url");
					string credential = server.Has("credential") ? server.GetString("credential") : "";
					ret.Add(new PeerConnection.IceServer(url, "", credential));
				}
				return ret;
			}
			catch (JSONException e)
			{
				throw new Exception("Error", e);
			}
		}

		// Request an attempt to drain the send queue, on a background thread.
		private void requestQueueDrainInBackground()
		{
			(new AsyncTaskAnonymousInnerClassHelper(this)).Execute();
		}

		private class AsyncTaskAnonymousInnerClassHelper : AsyncTask<string, int, string>
		{
			private readonly AppRTCClient outerInstance;

			public AsyncTaskAnonymousInnerClassHelper(AppRTCClient outerInstance)
			{
				this.outerInstance = outerInstance;
			}

			protected override string RunInBackground(string[] unused)
			{
				outerInstance.maybeDrainQueue();
				return null;
			}
		}

		// Send all queued messages if connected to the room.
		private void maybeDrainQueue()
		{
			lock (sendQueue)
			{
				if (appRTCSignalingParameters == null)
				{
					return;
				}
				try
				{
					foreach (string msg in sendQueue)
					{
						URLConnection connection = (new URL(appRTCSignalingParameters.gaeBaseHref + appRTCSignalingParameters.postMessageUrl)).OpenConnection();
						connection.DoOutput = true;
						connection.OutputStream.Write(msg.GetBytes("UTF-8"), 0, msg.Length - 1);
						if (!connection.GetHeaderField(null).StartsWith("HTTP/1.1 200 "))
						{
							throw new IOException("Non-200 response to POST: " + connection.GetHeaderField(null) + " for msg: " + msg);
						}
					}
				}
				catch (IOException e)
				{
					throw new Exception("Error", e);
				}
				sendQueue.Clear();
			}
		}

		// Return the contents of an InputStream as a String.
		private static string drainStream(System.IO.Stream inputStream)
		{
			Scanner s = (new Scanner(inputStream)).UseDelimiter("\\A");
			return s.HasNext ? s.Next() : "";
		}
	}

}