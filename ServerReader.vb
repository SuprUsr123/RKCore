' ServerReader.vb
' ReKindle Social API Client for VB.NET
' Complete wrapper for all social features - build custom clients and bots

Imports System
Imports System.Collections.Generic
Imports System.Net.Http
Imports System.Net.Http.Headers
Imports System.Text
Imports System.Text.Json
Imports System.Text.Json.Serialization
Imports System.Threading.Tasks
Imports System.Web

''' <summary>
''' Main client for interacting with ReKindle's social APIs.
''' Handles authentication, Cloud Functions, and moderation worker endpoints.
''' </summary>
Public Class ReKindleSocialClient
    Implements IDisposable

#Region "Configuration"
    Private _mainApiBase As String = "https://us-central1-rekindle-dd1fa.cloudfunctions.net/"
    Private _socialApiBase As String = "https://us-central1-rekindle-socials.cloudfunctions.net/"
    Private _moderationWorker As String = "https://rekindle-moderate.timjarnott.workers.dev"
    Private _translationWorker As String = "https://rekindle-translate.timjarnott.workers.dev"
    Private _rtdbBase As String = "https://rekindle-socials-default-rtdb.firebaseio.com/"
    Private _firestoreBase As String = "https://firestore.googleapis.com/v1/projects/rekindle-socials/databases/(default)/documents"
    Private _mainFirestoreBase As String = "https://firestore.googleapis.com/v1/projects/rekindle-dd1fa/databases/(default)/documents"
    Private _mainApiKey As String = String.Empty
    Private _socialApiKey As String = String.Empty
#End Region

#Region "Private Fields"
    Private ReadOnly _http As New HttpClient()
    Private ReadOnly _jsonOptions As JsonSerializerOptions
    Private _mainIdToken As String = String.Empty
    Private _socialIdToken As String = String.Empty
    Private _uid As String = String.Empty
    Private _email As String = String.Empty
    Private _isAuthenticated As Boolean = False
    Private _isSocialAuthenticated As Boolean = False
    Private _mainRefreshToken As String = String.Empty
    Private _socialRefreshToken As String = String.Empty
    Private _topicsCache As List(Of Topic)
    Private _topicsCacheTime As DateTime = DateTime.MinValue
    Private _postsCache As List(Of NeighbourhoodPost)
    Private _postsCacheTime As DateTime = DateTime.MinValue
    Private _topicCommentsCache As New Dictionary(Of String, List(Of TopicComment))
    Private _topicCommentsCacheTime As New Dictionary(Of String, DateTime)
    Private _postCommentsCache As New Dictionary(Of String, List(Of NeighbourhoodComment))
    Private _postCommentsCacheTime As New Dictionary(Of String, DateTime)
    Private _pollVotesCache As New Dictionary(Of String, Dictionary(Of Integer, Integer))
    Private _usernameCache As New Dictionary(Of String, String)
    Private Const CacheTtlSeconds As Integer = 300
#End Region

#Region "Constructors"
    Public Sub New(Optional configPath As String = Nothing)
        _http.Timeout = TimeSpan.FromSeconds(30)
        _http.DefaultRequestHeaders.Accept.Add(New MediaTypeWithQualityHeaderValue("application/json"))
        _http.DefaultRequestHeaders.Referrer = New Uri("https://rekindle.ink/")
        
        _jsonOptions = New JsonSerializerOptions With {
            .PropertyNameCaseInsensitive = True,
            .PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            .DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            .NumberHandling = JsonNumberHandling.AllowReadingFromString
        }
        
        LoadConfig(configPath)
    End Sub
    
    Private Sub LoadConfig(configPath As String)
        Dim paths As New List(Of String)
        If Not String.IsNullOrWhiteSpace(configPath) Then paths.Add(configPath)
        paths.Add(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "rekindle-config.json"))
        paths.Add(System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "rekindle-config.json"))
        paths.Add(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(GetType(ReKindleSocialClient).Assembly.Location), "rekindle-config.json"))
        
        For Each path In paths
            If Not System.IO.File.Exists(path) Then Continue For
            Try
                Dim json = System.IO.File.ReadAllText(path)
                Dim cfg = JsonSerializer.Deserialize(Of ReKindleConfig)(json, _jsonOptions)
                If cfg IsNot Nothing Then
                    If Not String.IsNullOrWhiteSpace(cfg.MainApiBase) Then _mainApiBase = cfg.MainApiBase
                    If Not String.IsNullOrWhiteSpace(cfg.SocialApiBase) Then _socialApiBase = cfg.SocialApiBase
                    If Not String.IsNullOrWhiteSpace(cfg.ModerationWorker) Then _moderationWorker = cfg.ModerationWorker
                    If Not String.IsNullOrWhiteSpace(cfg.TranslationWorker) Then _translationWorker = cfg.TranslationWorker
                    If Not String.IsNullOrWhiteSpace(cfg.RtdbBase) Then _rtdbBase = cfg.RtdbBase
                    If Not String.IsNullOrWhiteSpace(cfg.FirestoreBase) Then _firestoreBase = cfg.FirestoreBase.TrimEnd("/"c)
                    If Not String.IsNullOrWhiteSpace(cfg.MainFirestoreBase) Then _mainFirestoreBase = cfg.MainFirestoreBase.TrimEnd("/"c)
                    If cfg.Firebase IsNot Nothing Then
                        If cfg.Firebase.Main IsNot Nothing Then
                            _mainApiKey = cfg.Firebase.Main.ApiKey
                        End If
                        If cfg.Firebase.Social IsNot Nothing Then
                            _socialApiKey = cfg.Firebase.Social.ApiKey
                        End If
                    End If
                End If
                Exit For
            Catch
            End Try
        Next
    End Sub
#End Region

#Region "Authentication"
    ''' <summary>
    ''' Register a new user account.
    ''' </summary>
    Public Async Function RegisterUserAsync(username As String, password As String) As Task(Of RegisterResult)
        If String.IsNullOrWhiteSpace(username) OrElse String.IsNullOrWhiteSpace(password) Then
            Throw New ArgumentException("Username and password are required")
        End If
        
        If username.Length > 20 OrElse Not System.Text.RegularExpressions.Regex.IsMatch(username, "^[a-zA-Z0-9]+$") Then
            Throw New ArgumentException("Username must be ≤20 chars, alphanumeric only")
        End If

        Dim payload As New Dictionary(Of String, String) From {
            {"username", username},
            {"password", password}
        }
        
        Dim result = Await CallCloudFunctionAsync(Of RegisterResult)(_mainApiBase, "registerUser", payload, False)
        Return result
    End Function

    ''' <summary>
    ''' Exchange main project auth for social project token.
    ''' Must be called after authenticating to main project.
    ''' </summary>
    Public Async Function GetSocialTokenAsync() As Task(Of String)
        If Not _isAuthenticated Then Throw New InvalidOperationException("Not authenticated to main project")
        
        Dim result = Await CallCloudFunctionAsync(Of Dictionary(Of String, String))(_mainApiBase, "getSocialToken", Nothing, True)
        Return result("token")
    End Function

    ''' <summary>
    ''' Exchange a Firebase custom token for an ID token using the Identity Toolkit REST API.
    ''' Uses the main project API key from config.
    ''' </summary>
    Public Async Function SignInWithCustomTokenAsync(customToken As String) As Task(Of FirebaseAuthResponse)
        If String.IsNullOrWhiteSpace(_mainApiKey) Then
            Throw New InvalidOperationException("No main API key configured. Check rekindle-config.json.")
        End If
        
        Dim url = $"https://identitytoolkit.googleapis.com/v1/accounts:signInWithCustomToken?key={_mainApiKey}"
        Dim payload As New Dictionary(Of String, Object) From {
            {"token", customToken},
            {"returnSecureToken", True}
        }
        Dim json = JsonSerializer.Serialize(payload, _jsonOptions)
        Dim content = New StringContent(json, Encoding.UTF8, "application/json")
        
        Using request As New HttpRequestMessage(HttpMethod.Post, url)
            request.Content = content
            request.Headers.Authorization = Nothing
            Dim response = Await _http.SendAsync(request)
            Dim responseText = Await response.Content.ReadAsStringAsync()
            
            If Not response.IsSuccessStatusCode Then
                Throw New HttpRequestException($"Token exchange failed: {ExtractApiErrorMessage(responseText)}")
            End If
            
            Dim resp = JsonSerializer.Deserialize(Of FirebaseAuthResponse)(responseText, _jsonOptions)
            _mainRefreshToken = If(resp?.RefreshToken, String.Empty)
            Return resp
        End Using
    End Function

    ''' <summary>
    ''' Sign in with email/password against the main Firebase project using the Identity Toolkit REST API.
    ''' Accounts are created by the registerUser cloud function with email "{username}@rekindle.ink".
    ''' </summary>
    Public Async Function SignInWithPasswordAsync(email As String, password As String) As Task(Of FirebaseAuthResponse)
        If String.IsNullOrWhiteSpace(_mainApiKey) Then
            Throw New InvalidOperationException("No main API key configured. Check rekindle-config.json.")
        End If
        
        Dim url = $"https://identitytoolkit.googleapis.com/v1/accounts:signInWithPassword?key={_mainApiKey}"
        Dim payload As New Dictionary(Of String, Object) From {
            {"email", email},
            {"password", password},
            {"returnSecureToken", True}
        }
        Dim json = JsonSerializer.Serialize(payload, _jsonOptions)
        Dim content = New StringContent(json, Encoding.UTF8, "application/json")
        
        Using request As New HttpRequestMessage(HttpMethod.Post, url)
            request.Content = content
            request.Headers.Authorization = Nothing
            Dim response = Await _http.SendAsync(request)
            Dim responseText = Await response.Content.ReadAsStringAsync()
            
            If Not response.IsSuccessStatusCode Then
                Throw New HttpRequestException($"Login failed: {ExtractApiErrorMessage(responseText)}")
            End If
            
            Try
                Dim resp = JsonSerializer.Deserialize(Of FirebaseAuthResponse)(responseText, _jsonOptions)
                _mainRefreshToken = If(resp?.RefreshToken, String.Empty)
                Return resp
            Catch ex As Exception
                Throw New InvalidOperationException(
                    $"Failed to parse auth response. Raw body (first 500 chars): {If(responseText?.Length > 500, responseText.Substring(0, 500), responseText)}",
                    ex)
            End Try
        End Using
    End Function

    ''' <summary>
    ''' Exchange a Firebase custom token for a social project ID token.
    ''' Uses the social project API key from config.
    ''' </summary>
    Public Async Function SignInWithCustomTokenSocialAsync(customToken As String) As Task(Of FirebaseAuthResponse)
        If String.IsNullOrWhiteSpace(_socialApiKey) Then
            Throw New InvalidOperationException("No social API key configured. Check rekindle-config.json.")
        End If
        
        Dim url = $"https://identitytoolkit.googleapis.com/v1/accounts:signInWithCustomToken?key={_socialApiKey}"
        Dim payload As New Dictionary(Of String, Object) From {
            {"token", customToken},
            {"returnSecureToken", True}
        }
        Dim json = JsonSerializer.Serialize(payload, _jsonOptions)
        Dim content = New StringContent(json, Encoding.UTF8, "application/json")
        
        Using request As New HttpRequestMessage(HttpMethod.Post, url)
            request.Content = content
            request.Headers.Authorization = Nothing
            Dim response = Await _http.SendAsync(request)
            Dim responseText = Await response.Content.ReadAsStringAsync()
            
            If Not response.IsSuccessStatusCode Then
                Throw New HttpRequestException($"Social token exchange failed: {ExtractApiErrorMessage(responseText)}")
            End If
            
            Dim resp = JsonSerializer.Deserialize(Of FirebaseAuthResponse)(responseText, _jsonOptions)
            _socialRefreshToken = If(resp?.RefreshToken, String.Empty)
            Return resp
        End Using
    End Function

    ''' <summary>
    ''' Complete social auth: get the custom token from getSocialToken, exchange it for a
    ''' social project ID token (via signInWithCustomToken), and store it for RTDB/Firestore.
    ''' Mirrors the web client: socialAuth.signInWithCustomToken(getSocialToken().token).
    ''' </summary>
    Public Async Function AuthenticateSocialAsync() As Task(Of String)
        If Not _isAuthenticated Then Throw New InvalidOperationException("Not authenticated to main project")
        
        Dim customToken = Await GetSocialTokenAsync()
        Dim authResponse = Await SignInWithCustomTokenSocialAsync(customToken)
        If String.IsNullOrWhiteSpace(authResponse.IdToken) Then
            Throw New InvalidOperationException("Social token exchange returned no ID token")
        End If
        _socialIdToken = authResponse.IdToken
        _isSocialAuthenticated = True
        Return _socialIdToken
    End Function

    ''' <summary>
    ''' Set the main project ID token after Firebase authentication.
    ''' </summary>
    Public Sub SetMainIdToken(token As String, uid As String, email As String)
        _mainIdToken = token
        _uid = uid
        _email = email
        _isAuthenticated = True
    End Sub

    ''' <summary>
    ''' Exchange a refresh token for a fresh ID token (Identity Toolkit secureToken RPC).
    ''' </summary>
    Private Async Function RefreshIdTokenAsync(refreshToken As String, projectId As String) As Task(Of FirebaseAuthResponse)
        If String.IsNullOrWhiteSpace(refreshToken) Then
            Throw New InvalidOperationException("No refresh token available")
        End If
        
        Dim apiKey = If(projectId = "social", _socialApiKey, _mainApiKey)
        Dim url = $"https://securetoken.googleapis.com/v1/token?key={apiKey}"
        Dim payload As New Dictionary(Of String, String) From {
            {"grant_type", "refresh_token"},
            {"refresh_token", refreshToken}
        }
        Dim json = JsonSerializer.Serialize(payload)
        Using request As New HttpRequestMessage(HttpMethod.Post, url)
            request.Content = New StringContent(json, Encoding.UTF8, "application/json")
            request.Headers.Authorization = Nothing
            Dim response = Await _http.SendAsync(request)
            Dim body = Await response.Content.ReadAsStringAsync()
            If Not response.IsSuccessStatusCode Then
                Throw New HttpRequestException($"Token refresh failed: {ExtractApiErrorMessage(body)}")
            End If
            
            ' secureToken returns snake_case fields
            Dim raw = JsonDocument.Parse(body).RootElement
            Dim idToken = raw.GetProperty("id_token").GetString()
            Dim newRefresh = raw.GetProperty("refresh_token").GetString()
            Dim localId = If(raw.TryGetProperty("user_id", Nothing), raw.GetProperty("user_id").GetString(), "")
            Dim email = If(raw.TryGetProperty("email", Nothing), raw.GetProperty("email").GetString(), "")
            Return New FirebaseAuthResponse With {
                .IdToken = idToken,
                .RefreshToken = newRefresh,
                .LocalId = localId,
                .Email = email
            }
        End Using
    End Function

    Public Function GetMainRefreshToken() As String
        Return _mainRefreshToken
    End Function

    Public Function GetSocialRefreshToken() As String
        Return _socialRefreshToken
    End Function

    ''' <summary>
    ''' Restore a session from saved refresh tokens. Silently returns False if
    ''' refresh fails (expired/revoked) - caller falls back to the login screen.
    ''' </summary>
    Public Async Function RestoreSessionAsync(mainRefreshToken As String, socialRefreshToken As String, uid As String, email As String) As Task(Of Boolean)
        Try
            If String.IsNullOrWhiteSpace(mainRefreshToken) OrElse String.IsNullOrWhiteSpace(socialRefreshToken) Then Return False
            
            Dim mainAuth = Await RefreshIdTokenAsync(mainRefreshToken, "main")
            _mainIdToken = mainAuth.IdToken
            _mainRefreshToken = If(mainAuth.RefreshToken, mainRefreshToken)
            _uid = If(String.IsNullOrWhiteSpace(uid), mainAuth.LocalId, uid)
            _email = If(String.IsNullOrWhiteSpace(email), mainAuth.Email, email)
            _isAuthenticated = True
            
            Dim socialAuth = Await RefreshIdTokenAsync(socialRefreshToken, "social")
            _socialIdToken = socialAuth.IdToken
            _socialRefreshToken = If(socialAuth.RefreshToken, socialRefreshToken)
            _isSocialAuthenticated = True
            Return True
        Catch
            ' Invalid/expired refresh tokens - clear partial state
            _mainIdToken = String.Empty
            _socialIdToken = String.Empty
            _uid = String.Empty
            _email = String.Empty
            _isAuthenticated = False
            _isSocialAuthenticated = False
            Return False
        End Try
    End Function

    ''' <summary>
    ''' Set the social project ID token after Firebase authentication.
    ''' </summary>
    Public Sub SetSocialIdToken(token As String)
        _socialIdToken = token
        _isSocialAuthenticated = True
    End Sub

    Public ReadOnly Property IsAuthenticated As Boolean
        Get
            Return _isAuthenticated
        End Get
    End Property

    Public ReadOnly Property IsSocialAuthenticated As Boolean
        Get
            Return _isSocialAuthenticated
        End Get
    End Property

    Public ReadOnly Property UserId As String
        Get
            Return _uid
        End Get
    End Property

    Public ReadOnly Property Email As String
        Get
            Return _email
        End Get
    End Property

    Public ReadOnly Property MainApiKey As String
        Get
            Return _mainApiKey
        End Get
    End Property

    Public ReadOnly Property SocialApiKey As String
        Get
            Return _socialApiKey
        End Get
    End Property
#End Region

#Region "Diagnostics"
    ''' <summary>
    ''' Returns a summary of the loaded config (which keys/bases are present).
    ''' </summary>
    Public Function GetConfigStatus() As String
        Dim sb As New StringBuilder()
        sb.AppendLine($"Main API key   : {(If(String.IsNullOrWhiteSpace(_mainApiKey), "MISSING", "loaded"))}")
        sb.AppendLine($"Social API key : {(If(String.IsNullOrWhiteSpace(_socialApiKey), "MISSING", "loaded"))}")
        sb.AppendLine($"Main API base  : {_mainApiBase}")
        sb.AppendLine($"Social API base: {_socialApiBase}")
        sb.AppendLine($"Moderation wk  : {_moderationWorker}")
        sb.AppendLine($"Translation wk : {_translationWorker}")
        sb.AppendLine($"RTDB base      : {_rtdbBase}")
        sb.AppendLine($"Firestore base : {_firestoreBase}")
        Return sb.ToString()
    End Function

    ''' <summary>
    ''' Pings Firebase Identity Toolkit with the main API key to verify the key is valid
    ''' and the network path works. Returns the server's response/error text.
    ''' </summary>
    Public Async Function PingApiKeyAsync() As Task(Of String)
        If String.IsNullOrWhiteSpace(_mainApiKey) Then
            Return "API key is NOT configured (check rekindle-config.json)"
        End If
        
        Dim url = $"https://identitytoolkit.googleapis.com/v1/accounts:createAuthUri?key={_mainApiKey}"
        Dim payload As New Dictionary(Of String, Object) From {
            {"identifier", "ping@rekindle.ink"},
            {"continueUri", "https://rekindle.ink/"}
        }
        Dim json = JsonSerializer.Serialize(payload, _jsonOptions)
        Dim content = New StringContent(json, Encoding.UTF8, "application/json")
        
        Try
            Using request As New HttpRequestMessage(HttpMethod.Post, url)
                request.Content = content
                request.Headers.Authorization = Nothing
                Dim response = Await _http.SendAsync(request)
                Dim body = Await response.Content.ReadAsStringAsync()
                If response.IsSuccessStatusCode Then
                    Return "OK - API key accepted by Firebase"
                End If
                Return $"HTTP {CInt(response.StatusCode)} - {ExtractApiErrorMessage(body)}"
            End Using
        Catch ex As Exception
            Return $"Network error: {ex.Message}"
        End Try
    End Function

    ''' <summary>
    ''' Extracts the human-readable message from a Firebase/Identity Toolkit error JSON body.
    ''' </summary>
    Private Shared Function ExtractApiErrorMessage(body As String) As String
        If String.IsNullOrWhiteSpace(body) Then Return "(no response body)"
        Try
            Dim doc = JsonDocument.Parse(body)
            If doc.RootElement.ValueKind = JsonValueKind.Object Then
                Dim root = doc.RootElement
                If root.TryGetProperty("error", Nothing) Then
                    Dim err = root.GetProperty("error")
                    If err.ValueKind = JsonValueKind.Object Then
                        If err.TryGetProperty("message", Nothing) Then
                            Return err.GetProperty("message").GetString()
                        End If
                        If err.TryGetProperty("status", Nothing) Then
                            Return err.GetProperty("status").GetString()
                        End If
                    End If
                End If
                If root.TryGetProperty("message", Nothing) Then
                    Return root.GetProperty("message").GetString()
                End If
            End If
            If body.Length > 200 Then Return body.Substring(0, 200)
            Return body
        Catch
            If body.Length > 200 Then Return body.Substring(0, 200)
            Return body
        End Try
    End Function

    ''' <summary>
    ''' Tries to reach the main Cloud Functions base URL (diagnostic connectivity check).
    ''' </summary>
    Public Async Function PingMainApiAsync() As Task(Of String)
        Try
            Using req As New HttpRequestMessage(HttpMethod.Get, _mainApiBase)
                Dim response = Await _http.SendAsync(req)
                Return $"HTTP {CInt(response.StatusCode)}"
            End Using
        Catch ex As Exception
            Return $"Network error: {ex.Message}"
        End Try
    End Function
#End Region

#Region "Cloud Functions - Auth Status"
    ''' <summary>
    ''' Get main project user auth status (requires admin or moderator).
    ''' </summary>
    Public Async Function GetUserAuthStatusAsync(targetUid As String) As Task(Of UserAuthStatus)
        If Not _isAuthenticated Then Throw New InvalidOperationException("Not authenticated")
        
        Dim payload As New Dictionary(Of String, String) From {{"uid", targetUid}}
        Return Await CallCloudFunctionAsync(Of UserAuthStatus)(_mainApiBase, "getUserAuthStatus", payload, True)
    End Function

    ''' <summary>
    ''' Set main project user auth status (admin only).
    ''' </summary>
    Public Async Function SetUserAuthStatusAsync(targetUid As String, disabled As Boolean) As Task(Of Boolean)
        If Not _isAuthenticated Then Throw New InvalidOperationException("Not authenticated")
        
        Dim payload As New Dictionary(Of String, Object) From {
            {"uid", targetUid},
            {"disabled", disabled}
        }
        
        Dim result = Await CallCloudFunctionAsync(Of Dictionary(Of String, Object))(_mainApiBase, "setUserAuthStatus", payload, True)
        Return CBool(result("success"))
    End Function

    ''' <summary>
    ''' Get social project user auth status (requires admin or moderator).
    ''' </summary>
    Public Async Function GetSocialUserAuthStatusAsync(targetUid As String) As Task(Of UserAuthStatus)
        If Not _isAuthenticated Then Throw New InvalidOperationException("Not authenticated")
        
        Dim payload As New Dictionary(Of String, String) From {{"uid", targetUid}}
        Return Await CallCloudFunctionAsync(Of UserAuthStatus)(_mainApiBase, "getSocialUserAuthStatus", payload, True)
    End Function

    ''' <summary>
    ''' Set social project user auth status (admin only).
    ''' </summary>
    Public Async Function SetSocialUserAuthStatusAsync(targetUid As String, disabled As Boolean) As Task(Of Boolean)
        If Not _isAuthenticated Then Throw New InvalidOperationException("Not authenticated")
        
        Dim payload As New Dictionary(Of String, Object) From {
            {"uid", targetUid},
            {"disabled", disabled}
        }
        
        Dim result = Await CallCloudFunctionAsync(Of Dictionary(Of String, Object))(_mainApiBase, "setSocialUserAuthStatus", payload, True)
        Return CBool(result("success"))
    End Function

    ''' <summary>
    ''' Check IP on login - catches IP bans after account creation.
    ''' </summary>
    Public Async Function CheckIPOnLoginAsync() As Task(Of Boolean)
        If Not _isAuthenticated Then Throw New InvalidOperationException("Not authenticated")
        
        Dim result = Await CallCloudFunctionAsync(Of Dictionary(Of String, Boolean))(_mainApiBase, "checkIPOnLogin", Nothing, True)
        Return result("banned")
    End Function
#End Region

#Region "Cloud Functions - Age Verification"
    ''' <summary>
    ''' Self-declare age for social feature access.
    ''' </summary>
    Public Async Function VerifyAgeSelfDeclarationAsync(dob As Date, countryCode As String) As Task(Of AgeVerificationResult)
        If Not _isAuthenticated Then Throw New InvalidOperationException("Not authenticated")
        If String.IsNullOrWhiteSpace(countryCode) OrElse countryCode.Length <> 2 Then
            Throw New ArgumentException("Country code must be ISO 3166-1 alpha-2")
        End If
        
        Dim payload As New Dictionary(Of String, Object) From {
            {"dob", New Dictionary(Of String, Integer) From {
                {"day", dob.Day},
                {"month", dob.Month},
                {"year", dob.Year}
            }},
            {"country", countryCode.ToUpperInvariant()}
        }
        
        Return Await CallCloudFunctionAsync(Of AgeVerificationResult)(_mainApiBase, "verifyAgeSelfDeclaration", payload, True)
    End Function
#End Region

#Region "Moderation Worker - Content APIs"
    ''' <summary>
    ''' Send a KindleChat message.
    ''' </summary>
    Public Async Function SendChatMessageAsync(text As String, Optional replyTo As ChatReply = Nothing, 
                                               Optional pixelArt As String = Nothing, Optional flipnoteData As String = Nothing,
                                               Optional gridData As String = Nothing) As Task(Of ChatSendResult)
        If Not _isSocialAuthenticated Then Throw New InvalidOperationException("Not authenticated to social project")
        If String.IsNullOrWhiteSpace(text) AndAlso String.IsNullOrWhiteSpace(pixelArt) AndAlso String.IsNullOrWhiteSpace(flipnoteData) Then
            Throw New ArgumentException("Message must have text, pixel art, or flipnote")
        End If
        
        Dim payload As New Dictionary(Of String, Object) From {
            {"type", "kindlechat"},
            {"text", If(text, "")}
        }
        
        If replyTo IsNot Nothing Then
            payload.Add("replyTo", replyTo)
        End If
        
        If Not String.IsNullOrWhiteSpace(pixelArt) Then
            payload.Add("pixel_art", pixelArt)
            payload.Add("is_pixel_art", True)
        End If
        
        If Not String.IsNullOrWhiteSpace(gridData) Then
            payload.Add("grid_data", gridData)
        End If
        
        If Not String.IsNullOrWhiteSpace(flipnoteData) Then
            ' Parse JSON string into a proper Dictionary so it serializes as an object
            Dim flipObj = JsonSerializer.Deserialize(Of Dictionary(Of String, Object))(flipnoteData, _jsonOptions)
            If flipObj IsNot Nothing Then
                payload.Add("flipnote_data", flipObj)
            Else
                payload.Add("flipnote_data", flipnoteData)
            End If
            payload.Add("is_flipnote", True)
        End If
        
        Return Await CallWorkerAsync(Of ChatSendResult)(_moderationWorker, payload)
    End Function

    ''' <summary>
    ''' Create a new topic.
    ''' </summary>
    Public Async Function CreateTopicAsync(title As String, subheading As String, icon As String, Optional poll As TopicPoll = Nothing) As Task(Of TopicCreateResult)
        If Not _isSocialAuthenticated Then Throw New InvalidOperationException("Not authenticated to social project")
        If String.IsNullOrWhiteSpace(title) OrElse title.Length > 20 Then Throw New ArgumentException("Title must be 1-20 chars")
        If subheading IsNot Nothing AndAlso subheading.Length > 35 Then Throw New ArgumentException("Subheading max 35 chars")
        If String.IsNullOrWhiteSpace(icon) Then Throw New ArgumentException("Icon is required")
        
        Dim payload As New Dictionary(Of String, Object) From {
            {"type", "topic"},
            {"title", title},
            {"subheading", If(subheading, "")},
            {"icon", icon}
        }
        
        If poll IsNot Nothing Then
            If String.IsNullOrWhiteSpace(poll.Question) OrElse poll.Question.Length > 100 Then
                Throw New ArgumentException("Poll question must be 1-100 chars")
            End If
            If poll.Options Is Nothing OrElse poll.Options.Count < 2 OrElse poll.Options.Count > 4 Then
                Throw New ArgumentException("Poll must have 2-4 options")
            End If
            For Each opt In poll.Options
                If opt.Length > 50 Then Throw New ArgumentException("Poll options max 50 chars")
            Next
            payload.Add("poll", poll)
        End If
        
        Return Await CallWorkerAsync(Of TopicCreateResult)(_moderationWorker, payload)
    End Function

    ''' <summary>
    ''' Comment on a topic.
    ''' </summary>
    Public Async Function CommentOnTopicAsync(topicId As String, body As String) As Task(Of CommentCreateResult)
        If Not _isSocialAuthenticated Then Throw New InvalidOperationException("Not authenticated to social project")
        If String.IsNullOrWhiteSpace(topicId) Then Throw New ArgumentException("Topic ID required")
        If String.IsNullOrWhiteSpace(body) OrElse body.Length > 1000 Then Throw New ArgumentException("Body must be 1-1000 chars")
        
        Dim payload As New Dictionary(Of String, Object) From {
            {"type", "topic_comment"},
            {"topicId", topicId},
            {"body", body}
        }
        
        Return Await CallWorkerAsync(Of CommentCreateResult)(_moderationWorker, payload)
    End Function

    ''' <summary>
    ''' Create a neighbourhood post.
    ''' </summary>
    Public Async Function CreateNeighbourhoodPostAsync(text As String) As Task(Of NeighbourhoodPostResult)
        If Not _isSocialAuthenticated Then Throw New InvalidOperationException("Not authenticated to social project")
        If String.IsNullOrWhiteSpace(text) Then Throw New ArgumentException("Text required")
        
        ' Word count validation
        Dim wordCount = text.Split(New Char() {" "c, vbTab, vbCr, vbLf}, StringSplitOptions.RemoveEmptyEntries).Length
        If wordCount < 10 Then Throw New ArgumentException("Minimum 10 words required")
        If text.Length > 280 Then Throw New ArgumentException("Maximum 280 chars")
        
        Dim payload As New Dictionary(Of String, Object) From {
            {"type", "neighbourhood_post"},
            {"text", text}
        }
        
        Return Await CallWorkerAsync(Of NeighbourhoodPostResult)(_moderationWorker, payload)
    End Function

    ''' <summary>
    ''' Comment on a neighbourhood post.
    ''' </summary>
    Public Async Function CommentOnNeighbourhoodPostAsync(postId As String, text As String) As Task(Of CommentCreateResult)
        If Not _isSocialAuthenticated Then Throw New InvalidOperationException("Not authenticated to social project")
        If String.IsNullOrWhiteSpace(postId) Then Throw New ArgumentException("Post ID required")
        If String.IsNullOrWhiteSpace(text) OrElse text.Length < 2 OrElse text.Length > 200 Then
            Throw New ArgumentException("Comment must be 2-200 chars")
        End If
        
        Dim payload As New Dictionary(Of String, Object) From {
            {"type", "neighbourhood_comment"},
            {"postId", postId},
            {"text", text}
        }
        
        Return Await CallWorkerAsync(Of CommentCreateResult)(_moderationWorker, payload)
    End Function

    ''' <summary>
    ''' Report content (spam, harassment, etc.)
    ''' </summary>
    Public Async Function ReportContentAsync(report As ReportRequest) As Task(Of ReportResult)
        If Not _isSocialAuthenticated Then Throw New InvalidOperationException("Not authenticated to social project")
        If String.IsNullOrWhiteSpace(report.ContentId) Then Throw New ArgumentException("Content ID required")
        If String.IsNullOrWhiteSpace(report.ContentPath) Then Throw New ArgumentException("Content path required")
        If String.IsNullOrWhiteSpace(report.Reason) Then Throw New ArgumentException("Reason required")
        
        Dim payload As New Dictionary(Of String, Object) From {
            {"type", "report"},
            {"contentType", report.ContentType},
            {"contentId", report.ContentId},
            {"contentPath", report.ContentPath},
            {"reportedUserId", report.ReportedUserId},
            {"reason", report.Reason},
            {"comment", If(report.Comment, "")},
            {"contentSnapshot", If(report.ContentSnapshot, "")}
        }
        
        Return Await CallWorkerAsync(Of ReportResult)(_moderationWorker, payload)
    End Function

    ''' <summary>
    ''' Translate a chat message using the translation worker.
    ''' </summary>
    Public Async Function TranslateMessageAsync(uid As String, text As String, msgId As String, 
                                                 translateOnly As Boolean, missingLangs As List(Of String), 
                                                 sourceLang As String) As Task(Of TranslationResult)
        If Not _isSocialAuthenticated Then Throw New InvalidOperationException("Not authenticated to social project")
        
        Dim payload As New Dictionary(Of String, Object) From {
            {"uid", uid},
            {"text", text},
            {"msgId", msgId},
            {"translateOnly", translateOnly},
            {"missingLangs", missingLangs},
            {"sourceLang", sourceLang}
        }
        
        Return Await CallWorkerAsync(Of TranslationResult)(_translationWorker, payload)
    End Function
#End Region

#Region "RTDB Direct Reads"
    ''' <summary>
    ''' Read chat messages from RTDB with optional filtering.
    ''' </summary>
    Public Async Function GetChatMessagesAsync(Optional limit As Integer = 50, Optional beforeTimestamp As Long? = Nothing) As Task(Of List(Of ChatMessage))
        If Not _isSocialAuthenticated Then Throw New InvalidOperationException("Not authenticated to social project")
        
        Dim url As String
        If beforeTimestamp.HasValue Then
            url = $"{_rtdbBase}/kindlechat/messages.json?orderBy=%22timestamp%22&endAt={beforeTimestamp.Value - 1}&limitToLast={limit}"
        Else
            url = $"{_rtdbBase}/kindlechat/messages.json?orderBy=%22timestamp%22&limitToLast={limit}"
        End If
        
        Return Await FetchMessagesAsync(url)
    End Function

    ''' <summary>
    ''' Fetch messages newer than a given timestamp (for live polling).
    ''' Uses startAt which returns messages with timestamp >= the given value.
    ''' </summary>
    Public Async Function GetNewMessagesAsync(limit As Integer, afterTimestamp As Long) As Task(Of List(Of ChatMessage))
        If Not _isSocialAuthenticated Then Throw New InvalidOperationException("Not authenticated to social project")
        
        Dim url = $"{_rtdbBase}/kindlechat/messages.json?orderBy=%22timestamp%22&startAt={afterTimestamp + 1}&limitToFirst={limit}"
        Return Await FetchMessagesAsync(url)
    End Function

    Private Async Function FetchMessagesAsync(url As String) As Task(Of List(Of ChatMessage))
        Dim response = Await GetSocialStringAsync(url)
        If String.IsNullOrWhiteSpace(response) OrElse response = "null" Then Return New List(Of ChatMessage)()
        
        Dim dict = JsonSerializer.Deserialize(Of Dictionary(Of String, ChatMessage))(response, _jsonOptions)
        If dict Is Nothing Then Return New List(Of ChatMessage)()
        
        Return dict.Select(Function(kvp) New ChatMessage With {
            .Id = kvp.Key,
            .Text = kvp.Value.Text,
            .Uid = kvp.Value.Uid,
            .Username = kvp.Value.Username,
            .Timestamp = kvp.Value.Timestamp,
            .Reactions = kvp.Value.Reactions,
            .ReplyTo = kvp.Value.ReplyTo,
            .IsPixelArt = kvp.Value.IsPixelArt,
            .PixelArt = kvp.Value.PixelArt,
            .IsFlipnote = kvp.Value.IsFlipnote,
            .FlipnoteData = kvp.Value.FlipnoteData
        }).OrderBy(Function(m) m.Timestamp).ToList()
    End Function

    ''' <summary>
    ''' Get a single chat message by ID.
    ''' </summary>
    Public Async Function GetChatMessageAsync(messageId As String) As Task(Of ChatMessage)
        If Not _isSocialAuthenticated Then Throw New InvalidOperationException("Not authenticated to social project")
        If String.IsNullOrWhiteSpace(messageId) Then Throw New ArgumentException("Message ID required")
        
        Dim url = $"{_rtdbBase}/kindlechat/messages/{messageId}.json"
        Dim response = Await GetSocialStringAsync(url)
        If String.IsNullOrWhiteSpace(response) OrElse response = "null" Then Return Nothing
        
        Dim msg = JsonSerializer.Deserialize(Of ChatMessage)(response, _jsonOptions)
        If msg IsNot Nothing Then msg.Id = messageId
        Return msg
    End Function

    ''' <summary>
    ''' Get user public profile from RTDB.
    ''' </summary>
    Public Async Function GetUserProfileAsync(uid As String) As Task(Of UserProfile)
        If Not _isSocialAuthenticated Then Throw New InvalidOperationException("Not authenticated to social project")
        If String.IsNullOrWhiteSpace(uid) Then Throw New ArgumentException("UID required")
        
        Dim url = $"{_rtdbBase}/users_public/{uid}.json"
        Dim response = Await GetSocialStringAsync(url)
        If String.IsNullOrWhiteSpace(response) OrElse response = "null" Then Return Nothing
        
        Return JsonSerializer.Deserialize(Of UserProfile)(response, _jsonOptions)
    End Function

    ''' <summary>
    ''' Get user card (avatar + display name).
    ''' </summary>
    Public Async Function GetUserCardAsync(uid As String) As Task(Of UserCard)
        If Not _isSocialAuthenticated Then Throw New InvalidOperationException("Not authenticated to social project")
        If String.IsNullOrWhiteSpace(uid) Then Throw New ArgumentException("UID required")
        
        Dim url = $"{_rtdbBase}/user_cards/{uid}.json"
        Dim response = Await GetSocialStringAsync(url)
        If String.IsNullOrWhiteSpace(response) OrElse response = "null" Then Return Nothing
        
        Return JsonSerializer.Deserialize(Of UserCard)(response, _jsonOptions)
    End Function

    ''' <summary>
    ''' Resolve a UID to a display name (from user_cards), with per-session caching
    ''' so repeated lookups cost one RTDB read each at most. Mirrors the web app's
    ''' getKindleName()/user_cards lookup.
    ''' </summary>
    Public Async Function GetUsernameAsync(uid As String) As Task(Of String)
        If String.IsNullOrWhiteSpace(uid) Then Return "unknown"
        If _usernameCache.ContainsKey(uid) Then Return _usernameCache(uid)
        
        Dim name As String = uid.Substring(0, Math.Min(8, uid.Length))
        Try
            Dim card = Await GetUserCardAsync(uid)
            If card IsNot Nothing AndAlso Not String.IsNullOrWhiteSpace(card.Username) Then
                name = card.Username
            End If
        Catch
        End Try
        _usernameCache(uid) = name
        Return name
    End Function

    ''' <summary>
    ''' Resolve many UIDs to names. Batches through the cache, one RTDB read per new UID.
    ''' </summary>
    Public Async Function GetUsernamesAsync(uids As IEnumerable(Of String)) As Task(Of Dictionary(Of String, String))
        Dim result As New Dictionary(Of String, String)()
        For Each uid In uids.Distinct()
            result(uid) = Await GetUsernameAsync(uid)
        Next
        Return result
    End Function

    ''' <summary>
    ''' Get a user's pending reports (moderator/admin only).
    ''' </summary>
    Public Async Function GetPendingReportsAsync() As Task(Of List(Of ReportRecord))
        If Not _isSocialAuthenticated Then Throw New InvalidOperationException("Not authenticated to social project")
        
        Dim url = $"{_rtdbBase}/reports.json?orderBy=%22status%22&equalTo=%22pending%22"
        Dim response = Await GetSocialStringAsync(url)
        If String.IsNullOrWhiteSpace(response) OrElse response = "null" Then Return New List(Of ReportRecord)()
        
        Dim dict = JsonSerializer.Deserialize(Of Dictionary(Of String, ReportRecord))(response, _jsonOptions)
        If dict Is Nothing Then Return New List(Of ReportRecord)()
        
        Return dict.Select(Function(kvp) New ReportRecord With {
            .Id = kvp.Key,
            .ContentType = kvp.Value.ContentType,
            .ContentId = kvp.Value.ContentId,
            .ContentPath = kvp.Value.ContentPath,
            .ReportedUserId = kvp.Value.ReportedUserId,
            .ReporterUid = kvp.Value.ReporterUid,
            .Reason = kvp.Value.Reason,
            .Comment = kvp.Value.Comment,
            .ContentSnapshot = kvp.Value.ContentSnapshot,
            .Status = kvp.Value.Status,
            .Timestamp = kvp.Value.Timestamp
        }).ToList()
    End Function
#End Region

#Region "Firestore Direct Reads"
    ''' <summary>
    ''' Run a Firestore structured query (the same RPC the Firebase SDK uses for
    ''' .orderBy().limit().get()). Supports cursor pagination via startAfterValues.
    ''' parentPath = "" for a top-level collection, or a doc path like "topics/{id}"
    ''' to query a subcollection. collectionId must be a single segment (no "/").
    ''' </summary>
    Private Async Function RunFirestoreQueryAsync(parentPath As String, collectionId As String, orderByField As String, descending As Boolean, limit As Integer, Optional startAfterValues As List(Of Object) = Nothing) As Task(Of List(Of FirestoreDocument(Of Dictionary(Of String, FirestoreValue))))
        If String.IsNullOrWhiteSpace(_socialIdToken) Then
            Throw New InvalidOperationException("No social auth token available")
        End If
        
        Dim url = If(String.IsNullOrWhiteSpace(parentPath),
                     $"{_firestoreBase}:runQuery",
                     $"{_firestoreBase}/{parentPath}:runQuery")
        Dim direction = If(descending, "DESCENDING", "ASCENDING")
        Dim sq As New Dictionary(Of String, Object) From {
            {"from", New List(Of Object) From {
                New Dictionary(Of String, Object) From {{"collectionId", collectionId}}
            }},
            {"orderBy", New List(Of Object) From {
                New Dictionary(Of String, Object) From {
                    {"field", New Dictionary(Of String, Object) From {{"fieldPath", orderByField}}},
                    {"direction", direction}
                },
                New Dictionary(Of String, Object) From {
                    {"field", New Dictionary(Of String, Object) From {{"fieldPath", "__name__"}}},
                    {"direction", direction}
                }
            }},
            {"limit", limit}
        }
        If startAfterValues IsNot Nothing AndAlso startAfterValues.Count > 0 Then
            ' REST API has no "startAfter" field - exclusive cursor is startAt with before:false
            sq("startAt") = New Dictionary(Of String, Object) From {
                {"values", startAfterValues},
                {"before", False}
            }
        End If
        Dim query As New Dictionary(Of String, Object) From {
            {"structuredQuery", sq}
        }
        Dim json = JsonSerializer.Serialize(query, _jsonOptions)
        
        ' Retry on 429/5xx with exponential backoff
        Dim attempt As Integer = 0
        While True
            Using request As New HttpRequestMessage(HttpMethod.Post, url)
                request.Content = New StringContent(json, Encoding.UTF8, "application/json")
                request.Headers.Authorization = New AuthenticationHeaderValue("Bearer", _socialIdToken)
                Dim response = Await _http.SendAsync(request)
                Dim body = Await response.Content.ReadAsStringAsync()
                If response.IsSuccessStatusCode Then
                    Dim docs As New List(Of FirestoreDocument(Of Dictionary(Of String, FirestoreValue)))()
                    Try
                        Using doc = JsonDocument.Parse(body)
                            For Each el In doc.RootElement.EnumerateArray()
                                If el.TryGetProperty("document", Nothing) Then
                                    Dim d = el.GetProperty("document")
                                    Dim name = If(d.TryGetProperty("name", Nothing), d.GetProperty("name").GetString(), "")
                                    Dim fd As New Dictionary(Of String, FirestoreValue)
                                    If d.TryGetProperty("fields", Nothing) Then
                                        fd = d.GetProperty("fields").Deserialize(Of Dictionary(Of String, FirestoreValue))(_jsonOptions)
                                    End If
                                    docs.Add(New FirestoreDocument(Of Dictionary(Of String, FirestoreValue)) With {.Name = name, .Fields = fd})
                                End If
                            Next
                        End Using
                    Catch
                    End Try
                    Return docs
                End If
                
                Dim code = CInt(response.StatusCode)
                If (code = 429 OrElse code = 500 OrElse code = 503) AndAlso attempt < 5 Then
                    Dim delay = 2000 * Math.Pow(2, attempt)
                    attempt += 1
                    Console.ForegroundColor = ConsoleColor.DarkYellow
                    Console.WriteLine($"  ⏳ Firestore busy ({code}) - retrying in {CInt(delay / 1000)}s ({attempt}/5)...")
                    Console.ResetColor()
                    Await Task.Delay(CInt(delay))
                    Continue While
                End If
                
                Dim msg = If(body?.Length > 300, body.Substring(0, 300), body)
                Throw New HttpRequestException($"Firestore error {code}: {msg}")
            End Using
        End While
    End Function

    ''' <summary>
    ''' Get one page of topics (ordered by lastActive desc). Pass startAfterLastActive
    ''' (from the last item of the previous page) to fetch the next page ("See More").
    ''' Only fetches `limit` docs per call, keeping Firestore reads low.
    ''' </summary>
    Public Async Function GetTopicsPageAsync(limit As Integer, Optional startAfterLastActive As DateTime? = Nothing, Optional startAfterId As String = Nothing) As Task(Of List(Of Topic))
        If Not _isSocialAuthenticated Then Throw New InvalidOperationException("Not authenticated to social project")
        
        Dim cursor As List(Of Object) = Nothing
        If startAfterLastActive.HasValue Then
            Dim cursorVals As New List(Of Object) From {
                New Dictionary(Of String, String) From {{"timestampValue", startAfterLastActive.Value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ")}}
            }
            If Not String.IsNullOrWhiteSpace(startAfterId) Then
                cursorVals.Add(New Dictionary(Of String, Object) From {
                    {"referenceValue", $"projects/rekindle-socials/databases/(default)/documents/topics/{startAfterId}"}
                })
            End If
            cursor = cursorVals
        End If
        
        Dim docs = Await RunFirestoreQueryAsync("", "topics", "lastActive", True, limit, cursor)
        If docs Is Nothing OrElse docs.Count = 0 Then Return New List(Of Topic)()
        Dim result = docs.Select(Function(d) New Topic With {
            .Id = d.Name.Split("/"c).Last(),
            .Title = If(d.Fields.ContainsKey("title"), d.Fields("title").StringValue, "?"),
            .Subheading = If(d.Fields.ContainsKey("subheading"), d.Fields("subheading").StringValue, Nothing),
            .Icon = If(d.Fields.ContainsKey("icon"), d.Fields("icon").StringValue, Nothing),
            .AuthorId = If(d.Fields.ContainsKey("authorId"), d.Fields("authorId").StringValue, Nothing),
            .Timestamp = If(d.Fields.ContainsKey("timestamp"), d.Fields("timestamp").TimestampValue, Nothing),
            .LastActive = If(d.Fields.ContainsKey("lastActive"), d.Fields("lastActive").TimestampValue, Nothing),
            .CommentCount = If(d.Fields.ContainsKey("commentCount") AndAlso d.Fields("commentCount").IntegerValue.HasValue, d.Fields("commentCount").IntegerValue.Value, 0),
            .Poll = If(d.Fields.ContainsKey("poll") AndAlso d.Fields("poll").MapValue IsNot Nothing, New TopicPoll With {
                .Question = If(d.Fields("poll").MapValue.Fields.ContainsKey("question"), d.Fields("poll").MapValue.Fields("question").StringValue, Nothing),
                .Options = If(d.Fields("poll").MapValue.Fields.ContainsKey("options") AndAlso d.Fields("poll").MapValue.Fields("options").ArrayValue IsNot Nothing,
                    d.Fields("poll").MapValue.Fields("options").ArrayValue.Values.Select(Function(v) v.StringValue).ToList(),
                    New List(Of String)())
            }, Nothing)
        }).ToList()
        Return result
    End Function

    ''' <summary>
    ''' Get all topics (kept for compatibility, but prefer GetTopicsPageAsync).
    ''' </summary>
    Public Async Function GetTopicsAsync(Optional limit As Integer = 200, Optional forceRefresh As Boolean = False) As Task(Of List(Of Topic))
        If forceRefresh OrElse _topicsCache Is Nothing OrElse (DateTime.UtcNow - _topicsCacheTime).TotalSeconds >= CacheTtlSeconds Then
            _topicsCache = Await GetTopicsPageAsync(limit)
            _topicsCacheTime = DateTime.UtcNow
        End If
        Return _topicsCache
    End Function

    Public Async Function GetTopicCommentsAsync(topicId As String, Optional limit As Integer = 50, Optional forceRefresh As Boolean = False) As Task(Of List(Of TopicComment))
        If Not _isSocialAuthenticated Then Throw New InvalidOperationException("Not authenticated to social project")
        If String.IsNullOrWhiteSpace(topicId) Then Throw New ArgumentException("Topic ID required")
        
        ' Serve cached comments (5 min TTL) to avoid REST quota hits
        If Not forceRefresh AndAlso _topicCommentsCache.ContainsKey(topicId) AndAlso
           (DateTime.UtcNow - _topicCommentsCacheTime(topicId)).TotalSeconds < CacheTtlSeconds Then
            Return _topicCommentsCache(topicId)
        End If
        
        Dim docs = Await RunFirestoreQueryAsync($"topics/{topicId}", "comments", "timestamp", False, limit)
        If docs Is Nothing OrElse docs.Count = 0 Then Return New List(Of TopicComment)()
        
        Dim result = docs.Select(Function(d) New TopicComment With {
            .Id = d.Name.Split("/"c).Last(),
            .Body = If(d.Fields.ContainsKey("body"), d.Fields("body").StringValue, ""),
            .AuthorId = If(d.Fields.ContainsKey("authorId"), d.Fields("authorId").StringValue, Nothing),
            .Timestamp = If(d.Fields.ContainsKey("timestamp"), d.Fields("timestamp").TimestampValue, Nothing)
        }).ToList()
        _topicCommentsCache(topicId) = result
        _topicCommentsCacheTime(topicId) = DateTime.UtcNow
        Return result
    End Function

    ''' <summary>
    ''' Get one page of neighbourhood posts (timestamp desc). Pass startAfterTimestamp
    ''' + startAfterId (from the last item of the previous page) for the next page.
    ''' </summary>
    Public Async Function GetNeighbourhoodPostsPageAsync(limit As Integer, Optional startAfterTimestamp As DateTime? = Nothing, Optional startAfterId As String = Nothing) As Task(Of List(Of NeighbourhoodPost))
        If Not _isSocialAuthenticated Then Throw New InvalidOperationException("Not authenticated to social project")
        
        Dim cursor As List(Of Object) = Nothing
        If startAfterTimestamp.HasValue Then
            Dim cursorVals As New List(Of Object) From {
                New Dictionary(Of String, String) From {{"timestampValue", startAfterTimestamp.Value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ")}}
            }
            If Not String.IsNullOrWhiteSpace(startAfterId) Then
                cursorVals.Add(New Dictionary(Of String, Object) From {
                    {"referenceValue", $"projects/rekindle-socials/databases/(default)/documents/neighbourhood_posts/{startAfterId}"}
                })
            End If
            cursor = cursorVals
        End If
        
        Dim docs = Await RunFirestoreQueryAsync("", "neighbourhood_posts", "timestamp", True, limit, cursor)
        If docs Is Nothing OrElse docs.Count = 0 Then Return New List(Of NeighbourhoodPost)()
        
        Return docs.Select(Function(d) New NeighbourhoodPost With {
            .Id = d.Name.Split("/"c).Last(),
            .Uid = If(d.Fields.ContainsKey("uid"), d.Fields("uid").StringValue, Nothing),
            .Text = If(d.Fields.ContainsKey("text"), d.Fields("text").StringValue, ""),
            .Timestamp = If(d.Fields.ContainsKey("timestamp"), d.Fields("timestamp").TimestampValue, Nothing)
        }).ToList()
    End Function

    ''' <summary>
    ''' Get all neighbourhood posts (kept for compatibility, prefer page-based reads).
    ''' </summary>
    Public Async Function GetNeighbourhoodPostsAsync(Optional limit As Integer = 50, Optional forceRefresh As Boolean = False) As Task(Of List(Of NeighbourhoodPost))
        If forceRefresh OrElse _postsCache Is Nothing OrElse (DateTime.UtcNow - _postsCacheTime).TotalSeconds >= CacheTtlSeconds Then
            _postsCache = Await GetNeighbourhoodPostsPageAsync(limit)
            _postsCacheTime = DateTime.UtcNow
        End If
        Return _postsCache
    End Function

    Public Async Function GetNeighbourhoodCommentsAsync(postId As String, Optional limit As Integer = 50, Optional forceRefresh As Boolean = False) As Task(Of List(Of NeighbourhoodComment))
        If Not _isSocialAuthenticated Then Throw New InvalidOperationException("Not authenticated to social project")
        If String.IsNullOrWhiteSpace(postId) Then Throw New ArgumentException("Post ID required")
        
        If Not forceRefresh AndAlso _postCommentsCache.ContainsKey(postId) AndAlso
           (DateTime.UtcNow - _postCommentsCacheTime(postId)).TotalSeconds < CacheTtlSeconds Then
            Return _postCommentsCache(postId)
        End If
        
        Dim docs = Await RunFirestoreQueryAsync($"neighbourhood_posts/{postId}", "comments", "timestamp", True, limit)
        If docs Is Nothing OrElse docs.Count = 0 Then Return New List(Of NeighbourhoodComment)()
        
        Dim result = docs.Select(Function(d) New NeighbourhoodComment With {
            .Id = d.Name.Split("/"c).Last(),
            .Text = If(d.Fields.ContainsKey("text"), d.Fields("text").StringValue, ""),
            .AuthorId = If(d.Fields.ContainsKey("uid"), d.Fields("uid").StringValue, Nothing),
            .Timestamp = If(d.Fields.ContainsKey("timestamp"), d.Fields("timestamp").TimestampValue, Nothing)
        }).ToList()
        _postCommentsCache(postId) = result
        _postCommentsCacheTime(postId) = DateTime.UtcNow
        Return result
    End Function
#End Region

#Region "Main Firestore Reads (Art Library)"
    ''' <summary>
    ''' Read a Firestore document (raw JSON) from the MAIN project using the main ID token.
    ''' </summary>
    Private Async Function GetMainFirestoreDocAsync(path As String) As Task(Of String)
        If String.IsNullOrWhiteSpace(_mainIdToken) Then
            Throw New InvalidOperationException("Not authenticated to main project")
        End If
        Dim url = $"{_mainFirestoreBase}{path}"
        Return Await GetMainStringAsync(url)
    End Function

    ''' <summary>
    ''' Get the user's saved pixel art library (manifest) from Firestore.
    ''' </summary>
    Public Async Function GetPixelLibraryAsync() As Task(Of List(Of LibraryItem))
        Dim items As New List(Of LibraryItem)()
        If String.IsNullOrWhiteSpace(_uid) Then Return items
        
        Dim path = $"users/{_uid}/settings/pixel_manifest"
        Dim response = Await GetMainFirestoreDocAsync(path)
        If String.IsNullOrWhiteSpace(response) Then Return items
        
        Try
            Using doc = JsonDocument.Parse(response)
                Dim root = doc.RootElement
                If root.ValueKind = JsonValueKind.Object AndAlso root.TryGetProperty("fields", Nothing) Then
                    Dim fields = root.GetProperty("fields")
                    If fields.TryGetProperty("items", Nothing) Then
                        Dim itemsEl = fields.GetProperty("items")
                        If itemsEl.TryGetProperty("arrayValue", Nothing) Then
                            Dim arrayEl = itemsEl.GetProperty("arrayValue")
                            If arrayEl.TryGetProperty("values", Nothing) Then
                                For Each v In arrayEl.GetProperty("values").EnumerateArray()
                                    Dim li = New LibraryItem With {.Type = "pixel"}
                                    If v.TryGetProperty("mapValue", Nothing) Then
                                        Dim mf = v.GetProperty("mapValue").GetProperty("fields")
                                        li.Id = GetStringField(mf, "id")
                                        li.Title = GetStringField(mf, "title")
                                        li.Size = GetIntegerField(mf, "size")
                                        li.Thumbnail = GetStringField(mf, "thumbnail")
                                        li.Created = GetLongField(mf, "created")
                                        li.Modified = GetLongField(mf, "modified")
                                    End If
                                    If Not String.IsNullOrWhiteSpace(li.Id) Then items.Add(li)
                                Next
                            End If
                        End If
                    End If
                End If
            End Using
        Catch
        End Try
        Return items
    End Function

    ''' <summary>
    ''' Get a single saved pixel drawing's grid data from Firestore.
    ''' </summary>
    Public Async Function GetPixelDrawingAsync(id As String) As Task(Of String)
        If String.IsNullOrWhiteSpace(_uid) Then Return Nothing
        Dim path = $"users/{_uid}/pixel_drawings/{id}"
        Dim response = Await GetMainFirestoreDocAsync(path)
        If String.IsNullOrWhiteSpace(response) Then Return Nothing
        
        Try
            Using doc = JsonDocument.Parse(response)
                Dim root = doc.RootElement
                If root.ValueKind = JsonValueKind.Object AndAlso root.TryGetProperty("fields", Nothing) Then
                    Dim fields = root.GetProperty("fields")
                    If fields.TryGetProperty("grid", Nothing) Then
                        Dim gridEl = fields.GetProperty("grid")
                        If gridEl.TryGetProperty("stringValue", Nothing) Then
                            Return gridEl.GetProperty("stringValue").GetString()
                        End If
                    End If
                End If
            End Using
        Catch
        End Try
        Return Nothing
    End Function

    ''' <summary>
    ''' Get the user's saved flipnote library (manifest) from Firestore.
    ''' </summary>
    Public Async Function GetFlipnoteLibraryAsync() As Task(Of List(Of LibraryItem))
        Dim items As New List(Of LibraryItem)()
        If String.IsNullOrWhiteSpace(_uid) Then Return items
        
        Dim path = $"users/{_uid}/settings/flipnote_manifest"
        Dim response = Await GetMainFirestoreDocAsync(path)
        If String.IsNullOrWhiteSpace(response) Then Return items
        
        Try
            Using doc = JsonDocument.Parse(response)
                Dim root = doc.RootElement
                If root.ValueKind = JsonValueKind.Object AndAlso root.TryGetProperty("fields", Nothing) Then
                    Dim fields = root.GetProperty("fields")
                    If fields.TryGetProperty("items", Nothing) Then
                        Dim itemsEl = fields.GetProperty("items")
                        If itemsEl.TryGetProperty("arrayValue", Nothing) Then
                            Dim arrayEl = itemsEl.GetProperty("arrayValue")
                            If arrayEl.TryGetProperty("values", Nothing) Then
                                For Each v In arrayEl.GetProperty("values").EnumerateArray()
                                    Dim li = New LibraryItem With {.Type = "flipnote"}
                                    If v.TryGetProperty("mapValue", Nothing) Then
                                        Dim mf = v.GetProperty("mapValue").GetProperty("fields")
                                        li.Id = GetStringField(mf, "id")
                                        li.Title = GetStringField(mf, "title")
                                        li.FrameCount = GetIntegerField(mf, "frameCount")
                                        li.Thumbnail = GetStringField(mf, "thumbnail")
                                        li.Created = GetLongField(mf, "created")
                                        li.Modified = GetLongField(mf, "modified")
                                    End If
                                    If Not String.IsNullOrWhiteSpace(li.Id) Then items.Add(li)
                                Next
                            End If
                        End If
                    End If
                End If
            End Using
        Catch
        End Try
        Return items
    End Function

    ''' <summary>
    ''' Get a single saved flipnote animation data (JSON) from Firestore.
    ''' </summary>
    Public Async Function GetFlipnoteAnimationAsync(id As String) As Task(Of String)
        If String.IsNullOrWhiteSpace(_uid) Then Return Nothing
        Dim path = $"users/{_uid}/flipnote_animations/{id}"
        Dim response = Await GetMainFirestoreDocAsync(path)
        If String.IsNullOrWhiteSpace(response) Then Return Nothing
        
        Try
            Using doc = JsonDocument.Parse(response)
                Dim root = doc.RootElement
                If root.ValueKind = JsonValueKind.Object AndAlso root.TryGetProperty("fields", Nothing) Then
                    Dim fields = root.GetProperty("fields")
                    ' Rebuild { fps, frames } from Firestore fields
                    Dim fps As Integer = 0
                    If fields.TryGetProperty("fps", Nothing) Then
                        Dim fpsEl = fields.GetProperty("fps")
                        If fpsEl.TryGetProperty("integerValue", Nothing) Then
                            Integer.TryParse(fpsEl.GetProperty("integerValue").GetString(), fps)
                        End If
                    End If
                    Dim frames As New List(Of String)()
                    If fields.TryGetProperty("frames", Nothing) Then
                        Dim framesEl = fields.GetProperty("frames")
                        If framesEl.TryGetProperty("arrayValue", Nothing) Then
                            Dim arrayEl = framesEl.GetProperty("arrayValue")
                            If arrayEl.TryGetProperty("values", Nothing) Then
                                For Each v In arrayEl.GetProperty("values").EnumerateArray()
                                    If v.TryGetProperty("stringValue", Nothing) Then
                                        frames.Add(v.GetProperty("stringValue").GetString())
                                    End If
                                Next
                            End If
                        End If
                    End If
                    Dim result As New Dictionary(Of String, Object) From {
                        {"fps", fps},
                        {"frames", frames}
                    }
                    Return JsonSerializer.Serialize(result, _jsonOptions)
                End If
            End Using
        Catch
        End Try
        Return Nothing
    End Function

    Private Shared Function GetStringField(fields As JsonElement, name As String) As String
        If fields.ValueKind <> JsonValueKind.Object Then Return Nothing
        If Not fields.TryGetProperty(name, Nothing) Then Return Nothing
        Dim el = fields.GetProperty(name)
        If el.TryGetProperty("stringValue", Nothing) Then
            Return el.GetProperty("stringValue").GetString()
        End If
        Return Nothing
    End Function

    Private Shared Function GetIntegerField(fields As JsonElement, name As String) As Integer
        If fields.ValueKind <> JsonValueKind.Object Then Return 0
        If Not fields.TryGetProperty(name, Nothing) Then Return 0
        Dim el = fields.GetProperty(name)
        If el.TryGetProperty("integerValue", Nothing) Then
            Dim s = el.GetProperty("integerValue").GetString()
            Dim n As Integer
            If Integer.TryParse(s, n) Then Return n
        End If
        Return 0
    End Function

    Private Shared Function GetLongField(fields As JsonElement, name As String) As Long
        If fields.ValueKind <> JsonValueKind.Object Then Return 0
        If Not fields.TryGetProperty(name, Nothing) Then Return 0
        Dim el = fields.GetProperty(name)
        If el.TryGetProperty("integerValue", Nothing) Then
            Dim s = el.GetProperty("integerValue").GetString()
            Dim n As Long
            If Long.TryParse(s, n) Then Return n
        End If
        Return 0
    End Function
#End Region

#Region "RTDB Direct Writes"
    ''' <summary>
    ''' Add or toggle a reaction on a chat message.
    ''' </summary>
    Public Async Function SetReactionAsync(messageId As String, reaction As String) As Task
        If Not _isSocialAuthenticated Then Throw New InvalidOperationException("Not authenticated to social project")
        If String.IsNullOrWhiteSpace(messageId) Then Throw New ArgumentException("Message ID required")
        If String.IsNullOrWhiteSpace(reaction) Then Throw New ArgumentException("Reaction required")
        
        ' First check if reaction exists
        Dim checkUrl = $"{_rtdbBase}/kindlechat/messages/{messageId}/reactions/{_uid}.json"
        Dim checkResponse = Await GetSocialStringAsync(checkUrl)
        
        Dim payload As Object
        If String.IsNullOrWhiteSpace(checkResponse) OrElse checkResponse = "null" Then
            ' Add reaction
            payload = New Dictionary(Of String, Object) From {
                {"reaction", reaction},
                {"username", _email}
            }
            Dim content = New StringContent(JsonSerializer.Serialize(payload, _jsonOptions), Encoding.UTF8, "application/json")
            Await PutSocialAsync(checkUrl, content)
        Else
            ' Remove reaction (toggle off)
            Await DeleteSocialAsync(checkUrl)
        End If
    End Function

    ''' <summary>
    ''' Update a topic (title, subheading, icon) - author or mod only.
    ''' </summary>
    Public Async Function UpdateTopicAsync(topicId As String, title As String, subheading As String, icon As String) As Task
        If Not _isSocialAuthenticated Then Throw New InvalidOperationException("Not authenticated to social project")
        If String.IsNullOrWhiteSpace(topicId) Then Throw New ArgumentException("Topic ID required")
        
        Dim url = $"{_firestoreBase}/topics/{topicId}?updateMask.fieldPaths=title&updateMask.fieldPaths=subheading&updateMask.fieldPaths=icon"
        Dim payload As New Dictionary(Of String, Object)
        
        If Not String.IsNullOrWhiteSpace(title) Then
            payload.Add("title", New Dictionary(Of String, String) From {{"stringValue", title}})
        End If
        If subheading IsNot Nothing Then
            payload.Add("subheading", New Dictionary(Of String, String) From {{"stringValue", subheading}})
        End If
        If Not String.IsNullOrWhiteSpace(icon) Then
            payload.Add("icon", New Dictionary(Of String, String) From {{"stringValue", icon}})
        End If
        
        Dim content = New StringContent(JsonSerializer.Serialize(payload, _jsonOptions), Encoding.UTF8, "application/json")
        Dim response = Await PatchFirestoreAsync(url, content)
        response.EnsureSuccessStatusCode()
    End Function

    ''' <summary>
    ''' Delete a topic (author or moderator). Also removes its comments.
    ''' </summary>
    Public Async Function DeleteTopicAsync(topicId As String) As Task
        If Not _isSocialAuthenticated Then Throw New InvalidOperationException("Not authenticated to social project")
        If String.IsNullOrWhiteSpace(topicId) Then Throw New ArgumentException("Topic ID required")
        
        ' Delete the topic document (author/moderator/dev only, enforced by rules)
        Dim url = $"{_firestoreBase}/topics/{topicId}"
        Using request As New HttpRequestMessage(HttpMethod.Delete, url)
            If String.IsNullOrWhiteSpace(_socialIdToken) Then Throw New InvalidOperationException("No social auth token available")
            request.Headers.Authorization = New AuthenticationHeaderValue("Bearer", _socialIdToken)
            Dim response = Await _http.SendAsync(request)
            If Not response.IsSuccessStatusCode Then
                Dim body = Await response.Content.ReadAsStringAsync()
                Throw New HttpRequestException($"Delete failed ({CInt(response.StatusCode)}): {If(body?.Length > 300, body.Substring(0, 300), body)}")
            End If
        End Using
    End Function

    ''' <summary>
    ''' Delete a topic comment (author or moderator).
    ''' </summary>
    Public Async Function DeleteTopicCommentAsync(topicId As String, commentId As String) As Task
        If Not _isSocialAuthenticated Then Throw New InvalidOperationException("Not authenticated to social project")
        If String.IsNullOrWhiteSpace(topicId) Then Throw New ArgumentException("Topic ID required")
        If String.IsNullOrWhiteSpace(commentId) Then Throw New ArgumentException("Comment ID required")
        
        Dim url = $"{_firestoreBase}/topics/{topicId}/comments/{commentId}"
        Using request As New HttpRequestMessage(HttpMethod.Delete, url)
            If String.IsNullOrWhiteSpace(_socialIdToken) Then Throw New InvalidOperationException("No social auth token available")
            request.Headers.Authorization = New AuthenticationHeaderValue("Bearer", _socialIdToken)
            Dim response = Await _http.SendAsync(request)
            If Not response.IsSuccessStatusCode Then
                Dim body = Await response.Content.ReadAsStringAsync()
                Throw New HttpRequestException($"Delete failed ({CInt(response.StatusCode)}): {If(body?.Length > 300, body.Substring(0, 300), body)}")
            End If
        End Using
    End Function

    ''' <summary>
    ''' Delete a neighbourhood post (author or moderator).
    ''' </summary>
    Public Async Function DeleteNeighbourhoodPostAsync(postId As String) As Task
        If Not _isSocialAuthenticated Then Throw New InvalidOperationException("Not authenticated to social project")
        If String.IsNullOrWhiteSpace(postId) Then Throw New ArgumentException("Post ID required")
        
        Dim url = $"{_firestoreBase}/neighbourhood_posts/{postId}"
        Using request As New HttpRequestMessage(HttpMethod.Delete, url)
            If String.IsNullOrWhiteSpace(_socialIdToken) Then Throw New InvalidOperationException("No social auth token available")
            request.Headers.Authorization = New AuthenticationHeaderValue("Bearer", _socialIdToken)
            Dim response = Await _http.SendAsync(request)
            If Not response.IsSuccessStatusCode Then
                Dim body = Await response.Content.ReadAsStringAsync()
                Throw New HttpRequestException($"Delete failed ({CInt(response.StatusCode)}): {If(body?.Length > 300, body.Substring(0, 300), body)}")
            End If
        End Using
    End Function

    ''' <summary>
    ''' Delete a neighbourhood comment (author or moderator).
    ''' </summary>
    Public Async Function DeleteNeighbourhoodCommentAsync(postId As String, commentId As String) As Task
        If Not _isSocialAuthenticated Then Throw New InvalidOperationException("Not authenticated to social project")
        If String.IsNullOrWhiteSpace(postId) Then Throw New ArgumentException("Post ID required")
        If String.IsNullOrWhiteSpace(commentId) Then Throw New ArgumentException("Comment ID required")
        
        Dim url = $"{_firestoreBase}/neighbourhood_posts/{postId}/comments/{commentId}"
        Using request As New HttpRequestMessage(HttpMethod.Delete, url)
            If String.IsNullOrWhiteSpace(_socialIdToken) Then Throw New InvalidOperationException("No social auth token available")
            request.Headers.Authorization = New AuthenticationHeaderValue("Bearer", _socialIdToken)
            Dim response = Await _http.SendAsync(request)
            If Not response.IsSuccessStatusCode Then
                Dim body = Await response.Content.ReadAsStringAsync()
                Throw New HttpRequestException($"Delete failed ({CInt(response.StatusCode)}): {If(body?.Length > 300, body.Substring(0, 300), body)}")
            End If
        End Using
    End Function

    ''' <summary>
    ''' Get the vote counts for a topic's poll (reads all pollVotes).
    ''' Returns a dictionary of optionIndex -> count.
    ''' </summary>
    Public Async Function GetPollVotesAsync(topicId As String, Optional forceRefresh As Boolean = False) As Task(Of Dictionary(Of Integer, Integer))
        Dim result As New Dictionary(Of Integer, Integer)()
        If Not _isSocialAuthenticated Then Throw New InvalidOperationException("Not authenticated to social project")
        If String.IsNullOrWhiteSpace(topicId) Then Return result
        
        If Not forceRefresh AndAlso _pollVotesCache.ContainsKey(topicId) Then
            Return _pollVotesCache(topicId)
        End If
        
        ' pollVotes are keyed per-user with no ordering needed - run a plain query
        Dim docs = Await RunFirestoreQueryAsync($"topics/{topicId}", "pollVotes", "optionIndex", False, 500)
        If docs Is Nothing OrElse docs.Count = 0 Then Return result
        
        For Each d In docs
            If d.Fields IsNot Nothing AndAlso d.Fields.ContainsKey("optionIndex") AndAlso d.Fields("optionIndex").IntegerValue.HasValue Then
                Dim idx = d.Fields("optionIndex").IntegerValue.Value
                If result.ContainsKey(idx) Then
                    result(idx) += 1
                Else
                    result(idx) = 1
                End If
            End If
        Next
        _pollVotesCache(topicId) = result
        Return result
    End Function

    ''' <summary>
    ''' Delete a chat message (author or moderator).
    ''' </summary>
    Public Async Function DeleteChatMessageAsync(messageId As String) As Task
        If Not _isSocialAuthenticated Then Throw New InvalidOperationException("Not authenticated to social project")
        If String.IsNullOrWhiteSpace(messageId) Then Throw New ArgumentException("Message ID required")
        
        Dim url = $"{_rtdbBase}/kindlechat/messages/{messageId}.json"
        Dim response = Await DeleteSocialAsync(url)
        response.EnsureSuccessStatusCode()
    End Function

    ''' <summary>
    ''' Vote on a topic poll.
    ''' </summary>
    Public Async Function VoteOnPollAsync(topicId As String, optionIndex As Integer) As Task
        If Not _isSocialAuthenticated Then Throw New InvalidOperationException("Not authenticated to social project")
        If String.IsNullOrWhiteSpace(topicId) Then Throw New ArgumentException("Topic ID required")
        If optionIndex < 0 Then Throw New ArgumentException("Invalid option index")
        
        Dim url = $"{_firestoreBase}/topics/{topicId}/pollVotes/{_uid}"
        Dim payload As New Dictionary(Of String, Object) From {
            {"option", New Dictionary(Of String, Integer) From {{"integerValue", optionIndex}}},
            {"votedAt", New Dictionary(Of String, String) From {{"timestampValue", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")}}}
        }
        
        Dim content = New StringContent(JsonSerializer.Serialize(New Dictionary(Of String, Object) From {
            {"fields", payload}
        }, _jsonOptions), Encoding.UTF8, "application/json")
        
        Dim response = Await PatchFirestoreAsync(url, content)
        response.EnsureSuccessStatusCode()
        
        ' Invalidate poll vote cache so next view reflects the new vote
        If _pollVotesCache.ContainsKey(topicId) Then _pollVotesCache.Remove(topicId)
    End Function

    ''' <summary>
    ''' Drop the cached comments for a topic (call after posting/deleting a comment).
    ''' </summary>
    Public Sub InvalidateTopicComments(topicId As String)
        If _topicCommentsCache.ContainsKey(topicId) Then _topicCommentsCache.Remove(topicId)
    End Sub

    ''' <summary>
    ''' Drop the cached comments for a neighbourhood post (call after posting/deleting a comment).
    ''' </summary>
    Public Sub InvalidatePostComments(postId As String)
        If _postCommentsCache.ContainsKey(postId) Then _postCommentsCache.Remove(postId)
    End Sub
#End Region

#Region "Private Helpers"
    Private Function AddSocialAuth(url As String) As String
        If String.IsNullOrWhiteSpace(_socialIdToken) Then
            Throw New InvalidOperationException("No social auth token available")
        End If
        Dim separator = If(url.Contains("?"), "&", "?")
        Return $"{url}{separator}auth={_socialIdToken}"
    End Function

    Private Async Function GetMainStringAsync(url As String) As Task(Of String)
        Using request As New HttpRequestMessage(HttpMethod.Get, url)
            If String.IsNullOrWhiteSpace(_mainIdToken) Then
                Throw New InvalidOperationException("No main auth token available")
            End If
            request.Headers.Authorization = New AuthenticationHeaderValue("Bearer", _mainIdToken)
            Dim response = Await _http.SendAsync(request)
            Dim body = Await response.Content.ReadAsStringAsync()
            If Not response.IsSuccessStatusCode Then
                Throw New HttpRequestException($"Main request failed ({CInt(response.StatusCode)}): {If(body?.Length > 300, body.Substring(0, 300), body)}")
            End If
            Return body
        End Using
    End Function

    Private Async Function GetSocialStringAsync(url As String) As Task(Of String)
        Dim authUrl = AddSocialAuth(url)
        Using request As New HttpRequestMessage(HttpMethod.Get, authUrl)
            Dim response = Await _http.SendAsync(request)
            Dim body = Await response.Content.ReadAsStringAsync()
            If Not response.IsSuccessStatusCode Then
                Throw New HttpRequestException($"RTDB error {CInt(response.StatusCode)}: {If(body?.Length > 300, body.Substring(0, 300), body)}")
            End If
            Return body
        End Using
    End Function

    Private Async Function PutSocialAsync(url As String, content As HttpContent) As Task(Of HttpResponseMessage)
        Dim authUrl = AddSocialAuth(url)
        Return Await _http.PutAsync(authUrl, content)
    End Function

    Private Async Function DeleteSocialAsync(url As String) As Task(Of HttpResponseMessage)
        Dim authUrl = AddSocialAuth(url)
        Return Await _http.DeleteAsync(authUrl)
    End Function

    Private Async Function PatchSocialAsync(url As String, content As HttpContent) As Task(Of HttpResponseMessage)
        Dim authUrl = AddSocialAuth(url)
        Return Await _http.PatchAsync(authUrl, content)
    End Function

    Private Async Function GetFirestoreStringAsync(url As String) As Task(Of String)
        ' Retry on 429/5xx with exponential backoff (Firestore REST has low QPS quota)
        Dim attempt As Integer = 0
        Dim maxAttempts As Integer = 5
        While True
            Using request As New HttpRequestMessage(HttpMethod.Get, url)
                If String.IsNullOrWhiteSpace(_socialIdToken) Then
                    Throw New InvalidOperationException("No social auth token available")
                End If
                request.Headers.Authorization = New AuthenticationHeaderValue("Bearer", _socialIdToken)
                Dim response = Await _http.SendAsync(request)
                Dim body = Await response.Content.ReadAsStringAsync()
                If response.IsSuccessStatusCode Then
                    Return body
                End If
                
                Dim code = CInt(response.StatusCode)
                If (code = 429 OrElse code = 500 OrElse code = 503) AndAlso attempt < maxAttempts Then
                    ' Exponential backoff: 2s, 4s, 8s, 16s, 32s
                    Dim delay = 2000 * Math.Pow(2, attempt)
                    attempt += 1
                    Console.ForegroundColor = ConsoleColor.DarkYellow
                    Console.WriteLine($"  ⏳ Firestore busy ({code}) - retrying in {CInt(delay / 1000)}s ({attempt}/{maxAttempts})...")
                    Console.ResetColor()
                    Await Task.Delay(CInt(delay))
                    Continue While
                End If
                
                Dim msg = If(body?.Length > 300, body.Substring(0, 300), body)
                Throw New HttpRequestException($"Firestore error {code}: {msg}")
            End Using
        End While
    End Function

    Private Async Function PatchFirestoreAsync(url As String, content As HttpContent) As Task(Of HttpResponseMessage)
        Using request As New HttpRequestMessage(HttpMethod.Patch, url)
            request.Content = content
            If String.IsNullOrWhiteSpace(_socialIdToken) Then
                Throw New InvalidOperationException("No social auth token available")
            End If
            request.Headers.Authorization = New AuthenticationHeaderValue("Bearer", _socialIdToken)
            Return Await _http.SendAsync(request)
        End Using
    End Function

    Private Async Function CallCloudFunctionAsync(Of T)(apiBase As String, functionName As String, payload As Object, requireAuth As Boolean) As Task(Of T)
        Dim url = $"{apiBase}{functionName}"
        Dim content As HttpContent = Nothing
        
        ' Firebase v2 onCall protocol: wrap payload in { "data": ... }
        Dim wrappedPayload As New Dictionary(Of String, Object) From {
            {"data", If(payload IsNot Nothing, payload, New Object())}
        }
        Dim json = JsonSerializer.Serialize(wrappedPayload, _jsonOptions)
        content = New StringContent(json, Encoding.UTF8, "application/json")
        
        If requireAuth AndAlso Not _isAuthenticated Then
            Throw New InvalidOperationException("Authentication required")
        End If
        
        Using request As New HttpRequestMessage(HttpMethod.Post, url)
            request.Content = content
            If requireAuth AndAlso Not String.IsNullOrWhiteSpace(_mainIdToken) Then
                request.Headers.Authorization = New AuthenticationHeaderValue("Bearer", _mainIdToken)
            End If
            Dim response = Await _http.SendAsync(request)
            Dim responseText = Await response.Content.ReadAsStringAsync()
            
            If Not response.IsSuccessStatusCode Then
                Throw New HttpRequestException(
                    $"Cloud function {functionName} failed: {ExtractApiErrorMessage(responseText)}")
            End If
            
            ' v2 onCall returns { "result": <data> } on success
            Try
                Dim result = JsonSerializer.Deserialize(Of Dictionary(Of String, Object))(responseText, _jsonOptions)
                If result IsNot Nothing Then
                    ' v2 callable: { "result": ... }
                    If result.ContainsKey("result") Then
                        Dim dataJson = JsonSerializer.Serialize(result("result"), _jsonOptions)
                        Return JsonSerializer.Deserialize(Of T)(dataJson, _jsonOptions)
                    End If
                    ' v1 callable/legacy: { "data": ... }
                    If result.ContainsKey("data") Then
                        Dim dataJson = JsonSerializer.Serialize(result("data"), _jsonOptions)
                        Return JsonSerializer.Deserialize(Of T)(dataJson, _jsonOptions)
                    End If
                    ' Error keys
                    If result.ContainsKey("error") Then
                        Dim errMsg = result("error")?.ToString()
                        Throw New InvalidOperationException($"Server error: {errMsg}")
                    End If
                End If
            Catch ex As InvalidOperationException
                Throw
            Catch
            End Try
            
            ' Last resort: try deserializing the raw response
            Return JsonSerializer.Deserialize(Of T)(responseText, _jsonOptions)
        End Using
    End Function

    Private Async Function CallWorkerAsync(Of T)(workerUrl As String, payload As Object) As Task(Of T)
        Dim token = If(_socialIdToken, _mainIdToken)
        If String.IsNullOrWhiteSpace(token) Then
            Throw New InvalidOperationException("No auth token available")
        End If
        
        Dim json = JsonSerializer.Serialize(payload, _jsonOptions)
        Dim content = New StringContent(json, Encoding.UTF8, "application/json")
        
        If Not String.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("RK_DEBUG_PAYLOAD")) Then
            Try
                Dim logPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "rk-debug.log")
                System.IO.File.AppendAllText(logPath,
                    $"{DateTime.Now:HH:mm:ss} POST {workerUrl}{vbCrLf}{json}{vbCrLf}{vbCrLf}")
            Catch
            End Try
        End If
        
        Using request As New HttpRequestMessage(HttpMethod.Post, workerUrl)
            request.Content = content
            request.Headers.Authorization = New AuthenticationHeaderValue("Bearer", token)
            
            Dim response = Await _http.SendAsync(request)
            Dim responseText = Await response.Content.ReadAsStringAsync()
            
            If Not response.IsSuccessStatusCode Then
                Dim errorInfo = JsonSerializer.Deserialize(Of Dictionary(Of String, Object))(responseText, _jsonOptions)
                Dim errorMessage = If(errorInfo IsNot Nothing AndAlso errorInfo.ContainsKey("error"), 
                                    errorInfo("error")?.ToString(), 
                                    $"Worker returned {response.StatusCode}")
                Throw New HttpRequestException($"Worker error: {errorMessage}")
            End If
            
            Return JsonSerializer.Deserialize(Of T)(responseText, _jsonOptions)
        End Using
    End Function
#End Region

#Region "Dispose"
    Public Sub Dispose() Implements IDisposable.Dispose
        _http?.Dispose()
        GC.SuppressFinalize(Me)
    End Sub
#End Region

End Class

#Region "Data Models"

Public Class RegisterResult
    Public Property CustomToken As String
End Class

Public Class UserAuthStatus
    Public Property Disabled As Boolean?
    Public Property NotFound As Boolean?
End Class

Public Class AgeVerificationResult
    Public Property Success As Boolean
    Public Property Country As String
    Public Property Age As Integer
    Public Property MinimumAge As Integer
    Public Property Reason As String
End Class

Public Class ChatReply
    Public Property MsgId As String
    Public Property Uid As String
    Public Property Username As String
    Public Property Text As String
End Class

Public Class ChatSendResult
    Public Property Allowed As Boolean
    Public Property Key As String
End Class

Public Class LibraryItem
    Public Property Id As String
    Public Property Title As String
    Public Property Type As String
    Public Property Size As Integer
    Public Property FrameCount As Integer
    Public Property Thumbnail As String
    Public Property Created As Long
    Public Property Modified As Long
End Class

Public Class TopicPoll
    Public Property Question As String
    Public Property Options As List(Of String)
End Class

Public Class TopicCreateResult
    Public Property Success As Boolean
    Public Property Id As String
End Class

Public Class CommentCreateResult
    Public Property Success As Boolean
    Public Property Id As String
    Public Property CommentCount As Integer
End Class

Public Class NeighbourhoodPostResult
    Public Property Success As Boolean
    Public Property Id As String
End Class

Public Class ReportRequest
    Public Property ContentType As String
    Public Property ContentId As String
    Public Property ContentPath As String
    Public Property ReportedUserId As String
    Public Property Reason As String
    Public Property Comment As String
    Public Property ContentSnapshot As String
End Class

Public Class ReportResult
    Public Property Success As Boolean
    Public Property Id As String
End Class

Public Class TranslationResult
    Public Property Missing As List(Of String)
    Public Property SourceLang As String
End Class

Public Class ChatMessage
    Public Property Id As String
    Public Property Text As String
    Public Property Uid As String
    Public Property Username As String
    Public Property Timestamp As Long
    Public Property Reactions As Dictionary(Of String, Object)
    Public Property ReplyTo As ChatReply
    Public Property IsPixelArt As Boolean?
    Public Property PixelArt As String
    Public Property IsFlipnote As Boolean?
    Public Property FlipnoteData As String
End Class

Public Class UserProfile
    Public Property Username As String
    Public Property AvatarSeed As String
    Public Property CreatedAt As Long
End Class

Public Class UserCard
    Public Property Username As String
    Public Property AvatarSeed As String
End Class

Public Class ReportRecord
    Public Property Id As String
    Public Property ContentType As String
    Public Property ContentId As String
    Public Property ContentPath As String
    Public Property ReportedUserId As String
    Public Property ReporterUid As String
    Public Property Reason As String
    Public Property Comment As String
    Public Property ContentSnapshot As String
    Public Property Status As String
    Public Property Timestamp As Long
End Class

Public Class Topic
    Public Property Id As String
    Public Property Title As String
    Public Property Subheading As String
    Public Property Icon As String
    Public Property AuthorId As String
    Public Property Timestamp As DateTime?
    Public Property LastActive As DateTime?
    Public Property CommentCount As Integer
    Public Property Poll As TopicPoll
End Class

Public Class TopicComment
    Public Property Id As String
    Public Property Body As String
    Public Property AuthorId As String
    Public Property Timestamp As DateTime?
End Class

Public Class NeighbourhoodPost
    Public Property Id As String
    Public Property Uid As String
    Public Property Text As String
    Public Property Timestamp As DateTime?
End Class

Public Class NeighbourhoodComment
    Public Property Id As String
    Public Property Text As String
    Public Property AuthorId As String
    Public Property Timestamp As DateTime?
End Class

' Firestore wrapper classes
Public Class FirestoreQueryWrapper(Of T)
    Public Property Documents As List(Of FirestoreDocument(Of T))
End Class

Public Class FirestoreDocument(Of T)
    Public Property Name As String
    Public Property Fields As T
End Class

Public Class FirestoreTimestamp
    Public Property TimestampValue As DateTime?
End Class

Public Class FirestoreInteger
    Public Property IntegerValue As Integer?
End Class

Public Class FirestoreString
    Public Property StringValue As String
End Class

Public Class FirestoreMap
    Public Property MapValue As FirestoreMapValue
End Class

Public Class FirestoreMapValue
    Public Property Fields As Dictionary(Of String, FirestoreValue)
End Class

Public Class FirestoreArray
    Public Property ArrayValue As FirestoreArrayValue
End Class

Public Class FirestoreArrayValue
    Public Property Values As List(Of FirestoreValue)
End Class

Public Class FirestoreValue
    Public Property StringValue As String
    Public Property IntegerValue As Integer?
    Public Property BooleanValue As Boolean?
    Public Property TimestampValue As DateTime?
    Public Property MapValue As FirestoreMapValue
    Public Property ArrayValue As FirestoreArrayValue
End Class

#End Region

#Region "Config Models"
Public Class ReKindleConfig
    Public Property MainApiBase As String
    Public Property SocialApiBase As String
    Public Property ModerationWorker As String
    Public Property TranslationWorker As String
    Public Property RtdbBase As String
    Public Property FirestoreBase As String
    Public Property MainFirestoreBase As String
    Public Property Firebase As ReKindleFirebaseConfig
End Class

Public Class ReKindleFirebaseConfig
    Public Property Main As ReKindleFirebaseProjectConfig
    Public Property Social As ReKindleFirebaseProjectConfig
End Class

Public Class ReKindleFirebaseProjectConfig
    Public Property ApiKey As String
    Public Property AuthDomain As String
    Public Property ProjectId As String
    Public Property StorageBucket As String
    Public Property MessagingSenderId As String
    Public Property AppId As String
    Public Property DatabaseUrl As String
End Class

Public Class FirebaseAuthResponse
    Public Property IdToken As String
    Public Property Email As String
    Public Property RefreshToken As String
    Public Property ExpiresIn As String
    Public Property LocalId As String
    Public Property Registered As Boolean
End Class

#End Region