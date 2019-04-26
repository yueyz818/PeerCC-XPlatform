﻿using ClientCore.Account;
using ClientCore.Call;
using ClientCore.Signaling;
using GuiCore.Utilities;
using Org.WebRtc;
using PeerCC.Account;
using PeerCC.Signaling;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using WebRtcAdapter.Call;
using Windows.Data.Json;
using Windows.Storage;

namespace GuiCore
{
    public class GuiLogic
    {
        private static GuiLogic instance = null;
        private static readonly object InstanceLock = new object();

        public static GuiLogic Instance
        {
            get
            {
                lock (InstanceLock)
                {
                    if (instance == null)
                        instance = new GuiLogic();

                    return instance;
                }
            }
        }

        // SDP negotiation attributes
        public class NegotiationAtributes
        {
            public static readonly string SdpMid = "sdpMid";
            public static readonly string SdpMLineIndex = "sdpMLineIndex";
            public static readonly string Candidate = "candidate";
            public static readonly string Type = "type";
            public static readonly string Sdp = "sdp";
        }

        public class AccountModel
        {
            public string AccountName { get; set; }
            public string ServiceUri { get; set; }
            public string IdentityUri { get; set; }

            public AccountModel() { }
        }

        public ApplicationDataContainer localSettings =
            ApplicationData.Current.LocalSettings;

        public HttpSignaler _httpSignaler;

        public Account account;
        public Call call;

        private GuiLogic()
        {
            _httpSignaler = new HttpSignaler();

            _iceServers = new List<RTCIceServer>();

            AccountModel accountModel =
                XmlSerialization<AccountModel>.Deserialize((string)localSettings.Values["SelectedAccount"]);

            // Account 
            IAccountProvider accountFactory =
                ClientCore.Factory.SignalingFactory.Singleton.CreateIAccountProvider();

            AccountProvider accountProvider = (AccountProvider)accountFactory;

            account = (Account)accountProvider
                .GetAccount(accountModel?.ServiceUri, _httpSignaler.LocalPeer.Name, _httpSignaler);

            _httpSignaler = (HttpSignaler)account.Signaler;

            // Call
            ICallProvider callFactory =
                ClientCore.Factory.CallFactory.Singleton.CreateICallProvider();

            CallProvider callProvider = (CallProvider)callFactory;

            call = (Call)callProvider.GetCall();

            call.OnFrameRateChanged += (x, y) => { };
            call.OnResolutionChanged += (x, y) => { };

            // Media
            IMediaProvider mediaFactory =
                ClientCore.Factory.MediaFactory.Singleton.CreateMediaProvider();

            MediaProvider mediaProvider = (MediaProvider)mediaFactory;

            Media media = (Media)mediaProvider.GetMedia();

            media.GetCodecsAsync(MediaKind.Audio);
        }

        private readonly List<RTCIceServer> _iceServers;

        private readonly object _peerConnectionLock = new object();
        private RTCPeerConnection _peerConnection_DoNotUse;
        public RTCPeerConnection PeerConnection
        {
            get
            {
                lock (_peerConnectionLock)
                {
                    return _peerConnection_DoNotUse;
                }
            }
            set
            {
                lock (_peerConnectionLock)
                {
                    if (value == null)
                    {
                        if (_peerConnection_DoNotUse != null)
                        {
                            (_peerConnection_DoNotUse as IDisposable)?.Dispose();
                        }
                    }
                    _peerConnection_DoNotUse = value;
                }
            }
        }

        public void ConfigureIceServers(List<IceServer> iceServers)
        {
            _iceServers.Clear();

            foreach (IceServer iceServer in iceServers)
            {
                RTCIceServer server = new RTCIceServer();
                server.Urls = iceServer.Urls;
                server.Username = iceServer.Username;
                server.Credential = iceServer.Credential;
                _iceServers.Add(server);
            }
        }

        WebRtcFactory _factory;

        // Public events to notify about connection status
        public event Action OnPeerConnectionCreated;
        public event Action OnPeerConnectionClosed;
        public event Action OnReadyToConnect; 

        public Windows.UI.Xaml.Controls.MediaElement SelfVideo { get; set; }
        public Windows.UI.Xaml.Controls.MediaElement PeerVideo { get; set; }

        /// <summary>
        /// Creates a peer connection.
        /// </summary>
        /// <returns>True if connection to a peer is successfully created.</returns>
        public bool CreatePeerConnection()
        {
            Debug.Assert(PeerConnection == null);

            var factoryConfig = new WebRtcFactoryConfiguration();
            _factory = new WebRtcFactory(factoryConfig);

            var config = new RTCConfiguration()
            {
                Factory = _factory,
                BundlePolicy = RTCBundlePolicy.Balanced,
                IceTransportPolicy = RTCIceTransportPolicy.All,
                IceServers = _iceServers
            };

            Debug.WriteLine("Creating peer connection.");
            PeerConnection = new RTCPeerConnection(config);

            if (PeerConnection == null)
            {
                throw new NullReferenceException("Peer connection is not created.");
            }

            PeerConnection.OnIceGatheringStateChange += () =>
            {
                Debug.WriteLine("Ice connection state change, gathering-state = "
                    + PeerConnection.IceGatheringState.ToString().ToLower());
            };

            PeerConnection.OnIceConnectionStateChange += () =>
            {
                Debug.WriteLine("Ice connection state change, state="
                    + (PeerConnection != null ? PeerConnection.IceConnectionState.ToString().ToLower() : "closed"));
            };

            OnPeerConnectionCreated?.Invoke();

            PeerConnection.OnIceCandidate += PeerConnection_OnIceCandidate;
            PeerConnection.OnTrack += PeerConnection_OnTrack;
            PeerConnection.OnRemoveTrack += PeerConnection_OnRemoveTrack;

            GettingUserMedia();

            AddLocalMediaTracks();

            BindSelfVideo();

            return true;
        }

        private void GettingUserMedia()
        {
            Debug.WriteLine("Getting user media.");

            IReadOnlyList<IConstraint> mandatoryConstraints = new List<IConstraint>();
            //{
            //    new Constraint("maxWidth", Devices.Instance.VideoCaptureProfile.Width.ToString()),
            //    new Constraint("minWidth", Devices.Instance.VideoCaptureProfile.Width.ToString()),
            //    new Constraint("maxHeight", Devices.Instance.VideoCaptureProfile.Height.ToString()),
            //    new Constraint("minHeight", Devices.Instance.VideoCaptureProfile.Height.ToString()),
            //    new Constraint("maxFrameRate", Devices.Instance.VideoCaptureProfile.FrameRate.ToString()),
            //    new Constraint("minFrameRate", Devices.Instance.VideoCaptureProfile.FrameRate.ToString())
            //};

            IReadOnlyList<IConstraint> optionalConstraints = new List<IConstraint>();

            // TODO: select camera
            Devices.MediaDeviceModel _selectedVideoDevice = Devices.Instance.DeviceList[0];

            IMediaConstraints mediaConstraints = new MediaConstraints(mandatoryConstraints, optionalConstraints);

            var videoCapturer = VideoCapturer.Create(_selectedVideoDevice.Name, _selectedVideoDevice.Id, false);

            var videoOptions = new VideoOptions();
            videoOptions.Factory = _factory;
            videoOptions.Capturer = videoCapturer;
            videoOptions.Constraints = mediaConstraints;

            var videoTrackSource = VideoTrackSource.Create(videoOptions);
            _selfVideoTrack = MediaStreamTrack.CreateVideoTrack("SELF_VIDEO", videoTrackSource);

            var audioOptions = new AudioOptions();
            audioOptions.Factory = _factory;

            var audioTrackSource = AudioTrackSource.Create(audioOptions);
            _selfAudioTrack = MediaStreamTrack.CreateAudioTrack("SELF_AUDIO", audioTrackSource);
        }

        private void AddLocalMediaTracks()
        {
            Debug.WriteLine("Adding local media tracks.");
            PeerConnection.AddTrack(_selfVideoTrack);
            PeerConnection.AddTrack(_selfAudioTrack);

            OnAddLocalTrack?.Invoke(_selfVideoTrack);
            OnAddLocalTrack?.Invoke(_selfAudioTrack);
            //BindSelfVideo();
        }

        private void BindSelfVideo()
        {
            if (_selfVideoTrack != null)
            {
                //if (VideoLoopbackEnabled)
                //{
                    _selfVideoTrack.Element = MediaElementMaker.Bind(SelfVideo);
                    ((MediaStreamTrack)_selfVideoTrack).OnFrameRateChanged += (float frameRate) =>
                    {
                        FramesPerSecondChanged?.Invoke("SELF", frameRate.ToString("0.0"));
                    };
                    ((MediaStreamTrack)_selfVideoTrack).OnResolutionChanged += (uint width, uint height) =>
                    {
                        ResolutionChanged?.Invoke("SELF", width, height);
                    };
                //}
            }
        }

        private bool _cameraEnabled = true;
        private bool _microphoneIsOn = true;

        /// <summary>
        /// Add local media track event handler.
        /// </summary>
        /// <param name="track">Media track kind.</param>
        public void Instance_OnAddLocalTrack(IMediaStreamTrack track)
        {
            Debug.WriteLine("Add local track!");

            if (track.Kind == "audio")
            {
                if (_microphoneIsOn)
                {
                    Debug.WriteLine("audio!");
                }
            }
            if (track.Kind == "video")
            {
                if (_cameraEnabled)
                {
                    Debug.WriteLine("video!");
                    EnableLocalVideoStream();
                }
            }
        }

        public object MediaLock { get; set; } = new object();
        private bool VideoEnabled = true;

        /// <summary>
        /// Enables the local video stream.
        /// </summary>
        private void EnableLocalVideoStream()
        {
            lock (MediaLock)
            {
                if (_selfVideoTrack != null)
                    _selfVideoTrack.Enabled = true;
                VideoEnabled = true;
            }
        }

        /// <summary>
        /// Add remote media track event handler.
        /// </summary>
        /// <param name="track">Media track kind.</param>
        public void Instance_OnAddRemoteTrack(IMediaStreamTrack track)
        {
            Debug.WriteLine("MainPage: Add remote media track!");
        }

        bool _videoLoopbackEnabled = true;
        public bool VideoLoopbackEnabled
        {
            get
            {
                return _videoLoopbackEnabled;
            }
            set
            {
                if (_videoLoopbackEnabled == value) return;

                _videoLoopbackEnabled = value;
                if (_videoLoopbackEnabled)
                {
                    if (_selfVideoTrack != null)
                    {
                        Debug.WriteLine("Enabling video loopback.");

                        _selfVideoTrack.Element = MediaElementMaker.Bind(SelfVideo);
                        ((MediaStreamTrack)_selfVideoTrack).OnFrameRateChanged += (float frameRate) =>
                        {
                            FramesPerSecondChanged?.Invoke("SELF", frameRate.ToString("0.0"));
                        };
                        ((MediaStreamTrack)_selfVideoTrack).OnResolutionChanged += (uint width, uint height) => 
                        {
                            ResolutionChanged?.Invoke("SELF", width, height);
                        };

                        Debug.WriteLine("Video loopback enabled.");
                    }
                }
            }
        }

        public event Action<IMediaStreamTrack> OnAddLocalTrack;

        private IMediaStreamTrack _peerVideoTrack;
        private IMediaStreamTrack _selfVideoTrack;
        private IMediaStreamTrack _peerAudioTrack;
        private IMediaStreamTrack _selfAudioTrack;

        /// <summary>
        /// Logs in local peer to server.
        /// </summary>
        /// <returns></returns>
        public async Task LogInToServer()
        {
            Debug.WriteLine("Connects to server.");

            AccountModel account =
                    XmlSerialization<AccountModel>.Deserialize((string)localSettings.Values["SelectedAccount"]);

            await _httpSignaler.Connect(account.ServiceUri);
        }

        /// <summary>
        /// Logs out local peer from server.
        /// </summary>
        /// <returns></returns>
        public async Task LogOutFromServer()
        {
            Debug.WriteLine("Disconnects from server.");

            await _httpSignaler.SignOut();
        }

        private int _peerId = -1;

        /// <summary>
        /// Calls to connect to the selected peer.
        /// </summary>
        /// <param name="peerId">Remote peer id.</param>
        public async Task ConnectToPeer(int peerId)
        {
            Debug.Assert(_peerId == -1);

            if (PeerConnection != null)
            {
                Debug.WriteLine("[Error] We only support connection to one peer at a time.");
                return;
            }

            if (CreatePeerConnection())
            {
                _peerId = peerId;
                var offerOptions = new RTCOfferOptions();
                offerOptions.OfferToReceiveAudio = true;
                offerOptions.OfferToReceiveVideo = true;
                IRTCSessionDescription offer = await PeerConnection.CreateOffer(offerOptions);

                var audioCodecList = GetAudioCodecs();
                var videoCodecList = GetVideoCodecs();

                AudioCodec = audioCodecList.First();
                VideoCodec = videoCodecList.First();

                // Alter sdp to force usage of selected codecs
                string modifiedSdp = offer.Sdp;
                SdpUtils.SelectCodecs(ref modifiedSdp, AudioCodec.PreferredPayloadType, VideoCodec.PreferredPayloadType);
                var sdpInit = new RTCSessionDescriptionInit();
                sdpInit.Sdp = modifiedSdp;
                sdpInit.Type = offer.SdpType;
                var modifiedOffer = new RTCSessionDescription(sdpInit);

                await PeerConnection.SetLocalDescription(modifiedOffer);

                Debug.WriteLine($"Sending offer: {modifiedOffer.Sdp}");

                SendSdp(modifiedOffer);
            }
        }

        public static IList<CodecInfoModel> GetAudioCodecs()
        {
            var ret = new List<CodecInfoModel>
            {
                new CodecInfoModel { PreferredPayloadType = 111, ClockRate = 48000, Name = "opus" },
                new CodecInfoModel { PreferredPayloadType = 103, ClockRate = 16000, Name = "ISAC" },
                new CodecInfoModel { PreferredPayloadType = 104, ClockRate = 32000, Name = "ISAC" },
                new CodecInfoModel { PreferredPayloadType = 9, ClockRate = 8000, Name = "G722" },
                new CodecInfoModel { PreferredPayloadType = 102, ClockRate = 8000, Name = "ILBC" },
                new CodecInfoModel { PreferredPayloadType = 0, ClockRate = 8000, Name = "PCMU" },
                new CodecInfoModel { PreferredPayloadType = 8, ClockRate = 8000, Name = "PCMA" }
            };
            return ret;
        }

        public static IList<CodecInfoModel> GetVideoCodecs()
        {
            var ret = new List<CodecInfoModel>
            {
                new CodecInfoModel { PreferredPayloadType = 96, ClockRate = 90000, Name = "VP8" },
                new CodecInfoModel { PreferredPayloadType = 98, ClockRate = 90000, Name = "VP9" },
                new CodecInfoModel { PreferredPayloadType = 100, ClockRate = 90000, Name = "H264" }
            };
            return ret;
        }

        /// <summary>
        /// Video codec used in WebRTC session.
        /// </summary>
        public CodecInfoModel VideoCodec { get; set; }

        /// <summary>
        /// Audio codec used in WebRTC session.
        /// </summary>
        public CodecInfoModel AudioCodec { get; set; }

        public class CodecInfoModel
        {
            public byte PreferredPayloadType { get; set; }
            public string Name { get; set; }
            public int ClockRate { get; set; }
        }

        /// <summary>
        /// Calls to disconnect from peer.
        /// </summary>
        public void DisconnectFromPeer()
        {
            SendHangupMessage();

            ClosePeerConnection();
        }

        /// <summary>
        /// Helper method to send a hangup message to a peer.
        /// </summary>
        private void SendHangupMessage()
        {
            _httpSignaler.SendToPeer(_peerId, "BYE");
        }

        /// <summary>
        /// Closes a peer connection.
        /// </summary>
        private void ClosePeerConnection()
        {
            if (PeerConnection != null)
            {
                _peerId = -1;

                PeerConnection.OnIceCandidate -= PeerConnection_OnIceCandidate;
                PeerConnection.OnTrack -= PeerConnection_OnTrack;
                PeerConnection.OnRemoveTrack -= PeerConnection_OnRemoveTrack;

                PeerConnection = null;

                GC.Collect(); // Ensure all references are truly dropped.
            }
        }

        /// <summary>
        /// Invoked when the remote peer removed a media stream from the peer connection.
        /// </summary>
        public event Action<IMediaStreamTrack> OnRemoveRemoteTrack;

        private void PeerConnection_OnRemoveTrack(IRTCTrackEvent Event)
        {
            if (Event.Track.Kind == "video")
            {
                _peerVideoTrack.Element = null;
            }

            OnRemoveRemoteTrack?.Invoke(Event.Track);
        }

        public event Action<string, string> FramesPerSecondChanged;

        public event Action<string, uint, uint> ResolutionChanged;

        /// <summary>
        /// Invoked when the remote peer added media stream to the peer connection.
        /// </summary>
        public event Action<IMediaStreamTrack> OnAddRemoteTrack;

        private void PeerConnection_OnTrack(IRTCTrackEvent Event)
        {
            if (Event.Track.Kind == "video")
            {
                _peerVideoTrack = Event.Track;

                if (_peerVideoTrack != null)
                {
                    _peerVideoTrack.Element = MediaElementMaker.Bind(PeerVideo);
                    ((MediaStreamTrack)_peerVideoTrack).OnFrameRateChanged += (float frameRate) =>
                    {
                        FramesPerSecondChanged?.Invoke("PEER", frameRate.ToString("0.0"));
                    };
                    ((MediaStreamTrack)_peerVideoTrack).OnResolutionChanged += (uint width, uint height) =>
                    {
                        ResolutionChanged?.Invoke("PEER", width, height);
                    };
                }
            }
            else if (Event.Track.Kind == "audio")
            {
                _peerAudioTrack = Event.Track;
            }

            OnAddRemoteTrack?.Invoke(Event.Track);
        }

        /// <summary>
        /// Called when WebRTC detects another ICE candidate.
        /// This candidate needs to be sent to the other peer.
        /// </summary>
        /// <param name="Event">Details about RTCPeerConnectionIceEvent</param>
        private void PeerConnection_OnIceCandidate(IRTCPeerConnectionIceEvent Event)
        {
            if (Event.Candidate == null) return;

            double index = (double)Event.Candidate.SdpMLineIndex;

            JsonObject json = null;

            json = new JsonObject
            {
                { NegotiationAtributes.SdpMid, JsonValue.CreateStringValue(Event.Candidate.SdpMid) },
                { NegotiationAtributes.SdpMLineIndex, JsonValue.CreateNumberValue(index) },
                { NegotiationAtributes.Candidate, JsonValue.CreateStringValue(Event.Candidate.Candidate) }
            };

            Debug.WriteLine($"Send ice candidate:\n{json?.Stringify()}");

            SendMessage(json);
        }

        /// <summary>
        /// Sends SDP message.
        /// </summary>
        /// <param name="description">RTC session description.</param>
        private void SendSdp(IRTCSessionDescription description)
        {
            JsonObject json = null;
            Debug.WriteLine($"Sent session description: {description.Sdp}");

            json = new JsonObject();
            string messageType = null;

            switch (description.SdpType)
            {
                case RTCSdpType.Offer: messageType = "offer"; break;
                case RTCSdpType.Answer: messageType = "answer"; break;
                case RTCSdpType.Pranswer: messageType = "pranswer"; break;
                default: Debug.Assert(false, description.SdpType.ToString()); break;
            }

            json = new JsonObject
            {
                { NegotiationAtributes.Type, JsonValue.CreateStringValue(messageType) },
                { NegotiationAtributes.Sdp, JsonValue.CreateStringValue(description.Sdp) }
            };

            //return json;

            SendMessage(json);
        }

        public void MessageFromPeerTaskRun(Message message)
        {
            Debug.WriteLine("Message from peer!");
            Debug.WriteLine("Peer id: " + message.PeerId);
            Debug.WriteLine("Message id: " + message.Id);
            Debug.WriteLine("Message content: " + message.Content);

            int peerId = int.Parse(message.PeerId);
            string content = message.Content;

            Task.Run(async () => 
            {
                Debug.Assert(_peerId == peerId || _peerId == -1);
                Debug.Assert(content.Length > 0);

                if (_peerId != peerId && _peerId != -1)
                {
                    Debug.WriteLine("Received a message from unknown peer " +
                        "while already in a conversation with a different peer.");

                    return;
                }

                if (!JsonObject.TryParse(content, out JsonObject jMessage))
                {
                    Debug.WriteLine($"Received unknown message: {content}");
                    return;
                }

                string type = jMessage.ContainsKey(NegotiationAtributes.Type) 
                       ? jMessage.GetNamedString(NegotiationAtributes.Type) 
                       : null;

                if (PeerConnection == null)
                {
                    if (!string.IsNullOrEmpty(type))
                    {
                        // Create the peer connection only when call is 
                        // about to get initiated. Otherwise ignore the 
                        // message from peers which could be result 
                        // of old (but not yet fully closed) connections.
                        if (type == "offer" || type == "answer" || type == "json")
                        {
                            Debug.Assert(_peerId == -1);
                            _peerId = peerId;

                            if (!CreatePeerConnection())
                            {
                                Debug.WriteLine("Failed to initialize our PeerConnection instance");

                                await _httpSignaler.SignOut();
                                return;
                            }
                            else if (_peerId != peerId)
                            {
                                Debug.WriteLine("Received a message from unknown peer while already " +
                                    "in a conversation with a different peer.");

                                return;
                            }
                        }
                    }
                    else
                    {
                        Debug.WriteLine("[Warn] Received an untyped message after closing peer connection.");
                        return;
                    }
                }

                if (PeerConnection != null && !string.IsNullOrEmpty(type))
                {
                    if (type == "offer-loopback")
                    {
                        // Loopback not supported
                        Debug.Assert(false);
                    }

                    string sdp = null;

                    sdp = jMessage.ContainsKey(NegotiationAtributes.Sdp) 
                          ? jMessage.GetNamedString(NegotiationAtributes.Sdp) 
                          : null;

                    if (string.IsNullOrEmpty(sdp))
                    {
                        Debug.WriteLine("[Error] Can't parse received session description message.");
                        return;
                    }

                    Debug.WriteLine($"Received session description:\n{content}");

                    RTCSdpType messageType = RTCSdpType.Offer;
                    switch (type)
                    {
                        case "offer": messageType = RTCSdpType.Offer; break;
                        case "answer": messageType = RTCSdpType.Answer; break;
                        case "pranswer": messageType = RTCSdpType.Pranswer; break;
                        default: Debug.Assert(false, type); break;
                    }

                    var sdpInit = new RTCSessionDescriptionInit();
                    sdpInit.Sdp = sdp;
                    sdpInit.Type = messageType;
                    var description = new RTCSessionDescription(sdpInit);

                    await PeerConnection.SetRemoteDescription(description);

                    if (messageType == RTCSdpType.Offer)
                    {
                        var answerOptions = new RTCAnswerOptions();
                        IRTCSessionDescription answer = await PeerConnection.CreateAnswer(answerOptions);
                        await PeerConnection.SetLocalDescription(answer);
                        // Send answer
                        SendSdp(answer);
                    }
                }
                else
                {
                    RTCIceCandidate candidate = null;

                    string sdpMid = jMessage.ContainsKey(NegotiationAtributes.SdpMid) 
                           ? jMessage.GetNamedString(NegotiationAtributes.SdpMid) 
                           : null;

                    double sdpMLineIndex = jMessage.ContainsKey(NegotiationAtributes.SdpMLineIndex) 
                           ? jMessage.GetNamedNumber(NegotiationAtributes.SdpMLineIndex) 
                           : -1;

                    string sdpCandidate = jMessage.ContainsKey(NegotiationAtributes.Candidate) 
                           ? jMessage.GetNamedString(NegotiationAtributes.Candidate) 
                           : null;

                    if (string.IsNullOrEmpty(sdpMid) || sdpMLineIndex == -1 || string.IsNullOrEmpty(sdpCandidate))
                    {
                        Debug.WriteLine($"[Error] Can't parse received message.\n{content}");
                        return;
                    }

                    var candidateInit = new RTCIceCandidateInit();
                    candidateInit.Candidate = sdpCandidate;
                    candidateInit.SdpMid = sdpMid;
                    candidateInit.SdpMLineIndex = (ushort)sdpMLineIndex;
                    candidate = new RTCIceCandidate(candidateInit);

                    await PeerConnection.AddIceCandidate(candidate);

                    Debug.WriteLine($"Receiving ice candidate:\n{content}");
                }
            }).Wait();
        }

        private void SendMessage(IJsonValue json)
        {
            Debug.WriteLine($"Send message json: {json}");

            _httpSignaler.SendToPeer(_peerId, json.Stringify());
        }
    }
}
