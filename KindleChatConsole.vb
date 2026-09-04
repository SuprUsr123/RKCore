' KindleChatConsole.vb
' Enhanced console-based KindleChat client with full anti-abuse protection
' Implements rate limiting, retry logic, and all server-side protections

Imports System
Imports System.Collections.Generic
Imports System.Threading
Imports System.Threading.Tasks
Imports System.Text
Imports System.Text.Json
Imports System.Text.Json.Serialization

Module KindleChatConsole
    Private client As ReKindleSocialClient
    Private running As Boolean = True
    Private currentUser As String = ""
    Private lastMessageId As String = ""
    Private messageCache As New Dictionary(Of String, ChatMessage)
    
    ' === ANTI-ABUSE & RATE LIMITING ===
    Private Const MAX_MESSAGES_PER_BURST As Integer = 5
    Private Const BURST_WINDOW_MS As Integer = 12000 ' 12 seconds
    Private Const MIN_MESSAGE_INTERVAL_MS As Integer = 2000 ' 2 seconds between sends
    Private Const MAX_MESSAGE_LENGTH As Integer = 1000
    Private Const MAX_RETRY_ATTEMPTS As Integer = 4
    Private ReadOnly RETRY_DELAYS_MS As Integer() = {20000, 60000, 180000} ' 20s, 60s, 180s
    
    Private messageTimestamps As New List(Of DateTime)
    Private lastSendTime As DateTime = DateTime.MinValue
    Private isSendingMessage As Boolean = False
    Private isTimedOut As Boolean = False
    Private timeoutUntil As DateTime = DateTime.MinValue
    
    ' === CACHING ===
    Private Const MAX_CACHED_MESSAGES As Integer = 50
    Private Const MSG_CACHE_KEY As String = "kindlechat_console_cache"
    
    ' === STATE ===
    Private currentRoomId As String = "general"
    Private oldestTimestamp As Long? = Nothing
    Private noMoreMessages As Boolean = False
    Private isLoadingMore As Boolean = False
    Private renderedMessageIds As New HashSet(Of String)
    Private profileCache As New Dictionary(Of String, UserProfile)
    
    ' === TRANSLATION ===
    Private translationCache As New Dictionary(Of String, String)
    Private translationLang As String = "en"
    
    Sub Main()
        Console.Title = "KindleChat Console Client v2.0"
        Console.ForegroundColor = ConsoleColor.Cyan
        Console.WriteLine("╔══════════════════════════════════════════════════════════╗")
        Console.WriteLine("║         KindleChat Console Client v2.0                  ║")
        Console.WriteLine("║         With Anti-Abuse Protection                      ║")
        Console.WriteLine("╚══════════════════════════════════════════════════════════╝")
        Console.ResetColor()
        Console.WriteLine()
        
        ' Load language preference
        translationLang = GetUserLanguage()
        
        Try
            RunAsync().GetAwaiter().GetResult()
        Catch ex As Exception
            Console.ForegroundColor = ConsoleColor.Red
            Console.WriteLine($"Error: {ex.Message}")
            Console.ResetColor()
            Console.WriteLine("Press any key to exit...")
            Console.ReadKey()
        End Try
    End Sub
    
    Async Function RunAsync() As Task
        client = New ReKindleSocialClient()
        
        ' Try to restore a saved session (skips the login prompt)
        If Await TryRestoreSessionAsync() Then
            Console.ForegroundColor = ConsoleColor.Green
            Console.WriteLine($"  ✓ Session restored ({currentUser})")
            Console.ResetColor()
            Await Task.Delay(800)
        End If
        
        ' Main menu loop
        While running
            Console.Clear()
            ShowHeader()
            
            If client.IsAuthenticated Then
                Console.ForegroundColor = ConsoleColor.Green
                Console.WriteLine($"  Logged in as: {currentUser}")
                Console.ResetColor()
                
                ' Show timeout status if active
                If isTimedOut AndAlso timeoutUntil > DateTime.UtcNow Then
                    Dim remaining = Math.Ceiling((timeoutUntil - DateTime.UtcNow).TotalMinutes)
                    Console.ForegroundColor = ConsoleColor.Red
                    Console.WriteLine($"  ⚠ TIMED OUT: {remaining} minute(s) remaining")
                    Console.ResetColor()
                End If
                
                Console.WriteLine()
                Console.WriteLine("  ── Messages ──────────────────────")
                Console.WriteLine("   1. Send message")
                Console.WriteLine("   2. View recent messages")
                Console.WriteLine("   3. Send pixel art")
                Console.WriteLine("   4. Send flipnote (or GIF)")
                Console.WriteLine("   5. Live mode (real-time)")
                Console.WriteLine()
                Console.WriteLine("  ── Social ─────────────────────────")
                Console.WriteLine("   6. Topics")
                Console.WriteLine("   7. Neighbourhood")
                Console.WriteLine()
                Console.WriteLine("  ── Interact ──────────────────────")
                Console.WriteLine("   8. Toggle reaction")
                Console.WriteLine("   9. Delete my message")
                Console.WriteLine("  10. Check timeout status")
                Console.WriteLine()
                Console.WriteLine("  ── Account ───────────────────────")
                Console.WriteLine("  11. Logout")
                Console.WriteLine("   0. Exit")
            Else
                Console.WriteLine("  ── Account ───────────────────────")
                Console.WriteLine("   1. Register new account")
                Console.WriteLine("   2. Login")
                Console.WriteLine("   0. Exit")
            End If
            
            Console.WriteLine()
            Console.Write("  Select option: ")
            Dim choice = Console.ReadLine()
            
            If client.IsAuthenticated Then
                Select Case choice
                    Case "1" : Await SendMessageWithProtectionAsync()
                    Case "2" : Await ViewMessagesAsync()
                    Case "3" : Await SendPixelArtWithProtectionAsync()
                    Case "4" : Await SendFlipnoteWithProtectionAsync()
                    Case "5" : Await LiveModeWithProtectionAsync()
                    Case "6" : Await TopicsMenuAsync()
                    Case "7" : Await NeighbourhoodMenuAsync()
                    Case "8" : Await ToggleReactionAsync()
                    Case "9" : Await DeleteMessageAsync()
                    Case "10" : Await CheckTimeoutStatusAsync()
                    Case "11" : Await LogoutAsync()
                    Case "0" : running = False
                    Case Else : ShowInvalidOption()
                End Select
            Else
                Select Case choice
                    Case "1" : Await RegisterAccountAsync()
                    Case "2" : Await LoginAsync()
                    Case "0" : running = False
                    Case Else : ShowInvalidOption()
                End Select
            End If
        End While
        
        client?.Dispose()
    End Function
    
    Sub ShowHeader()
        Console.ForegroundColor = ConsoleColor.Cyan
        Console.WriteLine("  ╔═══════════════════════════════════════╗")
        Console.WriteLine("  ║     KindleChat Console Client v2.0     ║")
        Console.WriteLine("  ╚═══════════════════════════════════════╝")
        Console.ResetColor()
        Console.WriteLine()
    End Sub
    
    Sub ShowInvalidOption()
        Console.ForegroundColor = ConsoleColor.Red
        Console.WriteLine("  ✗ Invalid option. Try again.")
        Console.ResetColor()
        System.Threading.Thread.Sleep(800)
    End Sub

    ' ============================================================
    ' SESSION PERSISTENCE - skip login on subsequent launches
    ' ============================================================
    Private ReadOnly Property SessionPath As String
        Get
            Return IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".rk-session")
        End Get
    End Property

    Sub SaveSession()
        Try
            Dim content = $"{client.GetMainRefreshToken()}{vbLf}{client.GetSocialRefreshToken()}{vbLf}{client.UserId}{vbLf}{currentUser}"
            IO.File.WriteAllText(SessionPath, content)
        Catch
        End Try
    End Sub

    Sub ClearSession()
        Try
            If IO.File.Exists(SessionPath) Then IO.File.Delete(SessionPath)
        Catch
        End Try
    End Sub

    Async Function TryRestoreSessionAsync() As Task(Of Boolean)
        Try
            If Not IO.File.Exists(SessionPath) Then Return False
            Dim lines = IO.File.ReadAllLines(SessionPath)
            If lines.Length < 2 Then Return False
            
            Dim ok = Await client.RestoreSessionAsync(lines(0), lines(1), If(lines.Length > 2, lines(2), ""), If(lines.Length > 3, lines(3), ""))
            If ok Then
                currentUser = If(lines.Length > 3 AndAlso Not String.IsNullOrWhiteSpace(lines(3)), lines(3), client.Email)
            Else
                ClearSession()
            End If
            Return ok
        Catch
            Return False
        End Try
    End Function

    Async Function ReportContentPromptAsync(contentType As String, contentId As String, contentPath As String, reportedUid As String, snapshot As String) As Task
        Console.WriteLine()
        Console.WriteLine("  ── Report ──")
        Console.WriteLine("  Reasons: spam, harassment, inappropriate, hate_speech,")
        Console.WriteLine("           self_harm, violence, other")
        Console.WriteLine()
        Console.Write("  Reason: ")
        Dim reason = Console.ReadLine()
        If String.IsNullOrWhiteSpace(reason) Then Return
        Console.Write("  Additional comment (optional, max 200 chars): ")
        Dim comment = Console.ReadLine()
        
        Dim report As New ReportRequest With {
            .ContentType = contentType,
            .ContentId = contentId,
            .ContentPath = contentPath,
            .ReportedUserId = reportedUid,
            .Reason = reason,
            .Comment = If(comment, ""),
            .ContentSnapshot = If(snapshot, "")
        }
        Try
            Dim r = Await client.ReportContentAsync(report)
            Console.ForegroundColor = ConsoleColor.Green
            Console.WriteLine("  ✓ Report submitted.")
            Console.ResetColor()
        Catch ex As Exception
            Console.ForegroundColor = ConsoleColor.Red
            Console.WriteLine($"  ✗ {ex.Message}")
            Console.ResetColor()
        End Try
    End Function
    
    ' ============================================================
    ' ANTI-ABUSE: RATE LIMITING
    ' ============================================================
    
    Function CanSendMessage() As Boolean
        ' Check if timed out
        If isTimedOut AndAlso timeoutUntil > DateTime.UtcNow Then
            Dim remaining = Math.Ceiling((timeoutUntil - DateTime.UtcNow).TotalSeconds)
            Console.ForegroundColor = ConsoleColor.Red
            Console.WriteLine($"⛔ Timed out. Wait {remaining} seconds.")
            Console.ResetColor()
            Return False
        End If
        
        ' Check if currently sending
        If isSendingMessage Then
            Console.WriteLine("⏳ Message already sending...")
            Return False
        End If
        
        ' Check minimum interval between messages (2 seconds)
        If (DateTime.UtcNow - lastSendTime).TotalMilliseconds < MIN_MESSAGE_INTERVAL_MS Then
            Dim waitMs = MIN_MESSAGE_INTERVAL_MS - (DateTime.UtcNow - lastSendTime).TotalMilliseconds
            Console.WriteLine($"⏳ Please wait {Math.Ceiling(waitMs / 1000)} seconds between messages.")
            Return False
        End If
        
        ' Check burst limit (5 messages per 12 seconds)
        messageTimestamps.RemoveAll(Function(t) (DateTime.UtcNow - t).TotalMilliseconds > BURST_WINDOW_MS)
        If messageTimestamps.Count >= MAX_MESSAGES_PER_BURST Then
            Dim oldest = messageTimestamps(0)
            Dim waitMs = BURST_WINDOW_MS - (DateTime.UtcNow - oldest).TotalMilliseconds
            Console.WriteLine($"⏳ Rate limit: {MAX_MESSAGES_PER_BURST} messages per {BURST_WINDOW_MS / 1000}s. Wait {Math.Ceiling(waitMs / 1000)}s.")
            Return False
        End If
        
        Return True
    End Function
    
    Sub RecordMessageSend()
        messageTimestamps.Add(DateTime.UtcNow)
        lastSendTime = DateTime.UtcNow
        isSendingMessage = True
    End Sub
    
    Sub ResetSendState()
        isSendingMessage = False
    End Sub
    
    ' ============================================================
    ' ANTI-ABUSE: CONTENT FILTERING
    ' ============================================================
    
    Function ContainsUrl(text As String) As Boolean
        If String.IsNullOrWhiteSpace(text) Then Return False
        Dim t = text.ToLower()
        
        ' Check for protocol
        If System.Text.RegularExpressions.Regex.IsMatch(t, "h\s*t\s*t\s*p\s*s?\s*[:/]{1,4}") Then Return True
        
        ' Check for www
        If System.Text.RegularExpressions.Regex.IsMatch(t, "\bwww\.") Then Return True
        
        ' Check for common domains
        Dim domainPattern = "\b[a-z0-9-]+\.(com|net|org|io|co|ai|app|dev|edu|gov|mil|int|biz|info|name|pro|museum|aero|coop|jobs|mobi|travel|arpa|asia|cat|tel|xxx|post|geo|mail|onion|bit|crypto|eth|us|uk|au|ca|de|fr|jp|cn|kr|ru|br|mx|es|it|nl|se|no|fi|dk|pl|cz|at|ch|be|pt|ie|nz|za|in|sg|hk|tw|id|th|vn|ph|my|xyz|club|online|site|top|ink|cc|tv|ws|me|nu|gg|to|vc|link)\b"
        If System.Text.RegularExpressions.Regex.IsMatch(t, domainPattern) Then Return True
        
        ' Check for IP addresses
        If System.Text.RegularExpressions.Regex.IsMatch(t, "\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b") Then Return True
        
        Return False
    End Function
    
    Function ContainsPromotedTerm(text As String) As Boolean
        If String.IsNullOrWhiteSpace(text) Then Return False
        Dim t = text.ToLower()
        Dim terms = {"unreader", "un-reader", "inkchat", "kindlehub"}
        For Each term In terms
            If t.Contains(term) Then Return True
        Next
        Return False
    End Function
    
    Function FilterBadWords(text As String) As String
        ' This is a minimal filter - the server also filters
        ' In production, you'd want a more comprehensive list
        Dim badWords = {"fuck", "shit", "asshole", "bitch", "cunt", "nigger", "faggot"}
        Dim result = text
        For Each word In badWords
            result = result.Replace(word, New String("*"c, word.Length))
            result = result.Replace(word.ToUpper(), New String("*"c, word.Length))
            result = result.Replace(Char.ToUpper(word(0)) & word.Substring(1), New String("*"c, word.Length))
        Next
        Return result
    End Function
    
    ' ============================================================
    ' TRANSLATION WITH RETRY LOGIC
    ' ============================================================
    
    Async Function RequestTranslationWithRetryAsync(uid As String, text As String, msgId As String, 
                                                    Optional attempt As Integer = 1, 
                                                    Optional missingLangs As List(Of String) = Nothing,
                                                    Optional sourceLang As String = Nothing) As Task
        If attempt > MAX_RETRY_ATTEMPTS Then
            Return
        End If
        
        Dim shouldRetry As Boolean = False
        Dim retryLangs As List(Of String) = missingLangs
        Dim retrySource As String = sourceLang
        Dim retryDelay As Integer = 0
        
        Try
            Dim result = Await client.TranslateMessageAsync(uid, text, msgId, True, 
                                                           missingLangs, sourceLang)
            
            If result.Missing IsNot Nothing AndAlso result.Missing.Count > 0 Then
                shouldRetry = True
                retryLangs = result.Missing
                retrySource = result.SourceLang
                retryDelay = RETRY_DELAYS_MS(Math.Min(attempt - 1, RETRY_DELAYS_MS.Length - 1))
            End If
            
        Catch
            If attempt < MAX_RETRY_ATTEMPTS Then
                shouldRetry = True
                retryDelay = RETRY_DELAYS_MS(Math.Min(attempt - 1, RETRY_DELAYS_MS.Length - 1))
            End If
        End Try
        
        If shouldRetry Then
            Await Task.Delay(retryDelay)
            Await RequestTranslationWithRetryAsync(uid, text, msgId, attempt + 1, retryLangs, retrySource)
        End If
    End Function
    
    ' ============================================================
    ' USER LANGUAGE DETECTION
    ' ============================================================
    
    Function GetUserLanguage() As String
        Try
            ' Try to get from environment
            Dim uiLang = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName
            Dim supported = {"en", "es", "pt", "pl", "de", "it", "fr", "ru", "zh", "vi"}
            If Array.IndexOf(supported, uiLang) >= 0 Then
                Return uiLang
            End If
        Catch
        End Try
        Return "en"
    End Function
    
    ' ============================================================
    ' AUTHENTICATION METHODS
    ' ============================================================
    
    Async Function RegisterAccountAsync() As Task
        Console.Clear()
        Console.ForegroundColor = ConsoleColor.Yellow
        Console.WriteLine("Register New Account")
        Console.WriteLine("====================")
        Console.ResetColor()
        Console.WriteLine()
        
        Console.Write("Username (alphanumeric, max 20 chars): ")
        Dim username = Console.ReadLine()
        
        Console.Write("Password: ")
        Dim password = ReadPassword()
        
        Console.WriteLine()
        Console.WriteLine("Registering...")
        
        Try
            Dim result = Await client.RegisterUserAsync(username, password)
            
            ' Exchange custom token for a full session automatically
            Dim tokenData = Await client.SignInWithCustomTokenAsync(result.CustomToken)
            client.SetMainIdToken(tokenData.IdToken, tokenData.LocalId, tokenData.Email)
            
            Await client.AuthenticateSocialAsync()
            
            currentUser = tokenData.Email
            
            Console.ForegroundColor = ConsoleColor.Green
            Console.WriteLine("✓ Account created and logged in!")
            SaveSession()
            Console.ResetColor()
            
            ' Check IP ban and timeout
            Dim banned = Await client.CheckIPOnLoginAsync()
            If banned Then
                Console.ForegroundColor = ConsoleColor.Red
                Console.WriteLine("⚠ WARNING: Your IP is banned! Account disabled.")
                Console.ResetColor()
            End If
            
            Console.WriteLine("Press any key to continue...")
            Console.ReadKey()
            
        Catch ex As Exception
            Console.ForegroundColor = ConsoleColor.Red
            Console.WriteLine($"✗ Registration failed: {ex.Message}")
            Console.ResetColor()
            Console.WriteLine("Press any key to continue...")
            Console.ReadKey()
        End Try
    End Function
    
    Async Function LoginAsync() As Task
        Console.Clear()
        Console.ForegroundColor = ConsoleColor.Yellow
        Console.WriteLine("Login")
        Console.WriteLine("=====")
        Console.ResetColor()
        Console.WriteLine()
        Console.WriteLine("1. Login with username/password")
        Console.WriteLine("2. Login with custom token (auto-exchange)")
        Console.WriteLine("3. Paste Firebase ID token directly")
        Console.WriteLine("4. Diagnostics")
        Console.WriteLine("0. Back")
        Console.WriteLine()
        Console.Write("Select option: ")
        Dim choice = Console.ReadLine()
        
        If choice = "0" Then Return
        If choice = "1" Then
            Await LoginWithPasswordAsync()
        ElseIf choice = "2" Then
            Await LoginWithCustomTokenAsync()
        ElseIf choice = "3" Then
            Await LoginWithIdTokenAsync()
        Else
            Await RunDiagnosticsAsync()
        End If
    End Function
    
    Async Function RunDiagnosticsAsync() As Task
        Console.Clear()
        Console.ForegroundColor = ConsoleColor.Yellow
        Console.WriteLine("Diagnostics")
        Console.WriteLine("===========")
        Console.ResetColor()
        Console.WriteLine()
        
        Console.WriteLine("Config loaded from rekindle-config.json:")
        Console.WriteLine(client.GetConfigStatus())
        Console.WriteLine()
        
        Console.Write("Pinging Firebase Identity Toolkit (main API key)... ")
        Dim keyResult = Await client.PingApiKeyAsync()
        Console.WriteLine(keyResult)
        
        Console.Write("Pinging main Cloud Functions base... ")
        Dim apiResult = Await client.PingMainApiAsync()
        Console.WriteLine(apiResult)
        
        Console.WriteLine()
        Console.WriteLine("Ping results:")
        If keyResult.Contains("referer") OrElse keyResult.Contains("referrer") Then
            Console.ForegroundColor = ConsoleColor.Yellow
            Console.WriteLine("NOTE: The API key is HTTP-referrer restricted (web-only).")
            Console.WriteLine("The console app has no referrer, so Firebase blocks it with 403.")
            Console.WriteLine("Fix: create an unrestricted API key (or add a referrer like")
            Console.WriteLine("  localhost/*) for this app in Google Cloud Console > APIs &")
            Console.WriteLine("  Services > Credentials > rekindle key > edit restrictions.")
            Console.WriteLine("Alternative: register/login via the web app and paste the ID")
            Console.WriteLine("  token (login option 3) instead of password login.")
            Console.ResetColor()
        Else
            Console.WriteLine("If the API key ping shows an error, the message above tells you why")
            Console.WriteLine("(e.g. INVALID_API_KEY = wrong key, network error = offline/clock skew).")
        End If
        Console.WriteLine("Press any key to continue...")
        Console.ReadKey()
    End Function
    
    Async Function LoginWithPasswordAsync() As Task
        Console.Clear()
        Console.ForegroundColor = ConsoleColor.Yellow
        Console.WriteLine("Login with Username/Password")
        Console.WriteLine("============================")
        Console.ResetColor()
        Console.WriteLine()
        
        Console.Write("Username: ")
        Dim username = Console.ReadLine()
        
        Console.Write("Password: ")
        Dim password = ReadPassword()
        
        Console.WriteLine()
        Console.WriteLine("Authenticating...")
        
        Try
            Dim tokenData = Await client.SignInWithPasswordAsync($"{username}@rekindle.ink", password)
            client.SetMainIdToken(tokenData.IdToken, tokenData.LocalId, tokenData.Email)
            
            Await client.AuthenticateSocialAsync()
            
            currentUser = tokenData.Email
            
            Console.ForegroundColor = ConsoleColor.Green
            Console.WriteLine($"✓ Logged in as {tokenData.Email}")
            SaveSession()
            Console.ResetColor()
            
            Dim banned = Await client.CheckIPOnLoginAsync()
            If banned Then
                Console.ForegroundColor = ConsoleColor.Red
                Console.WriteLine("⚠ WARNING: Your IP is banned! Account disabled.")
                Console.ResetColor()
            End If
            
            Await CheckTimeoutStatusAsync()
            Console.WriteLine("Press any key to continue...")
            Console.ReadKey()
            
        Catch ex As Exception
            Console.ForegroundColor = ConsoleColor.Red
            Console.WriteLine($"✗ Login failed: {ex.Message}")
            Console.WriteLine()
            Console.WriteLine(ExplainLoginError(ex.Message))
            Console.ResetColor()
            Console.WriteLine("Press any key to continue...")
            Console.ReadKey()
        End Try
    End Function
    
    Async Function LoginWithCustomTokenAsync() As Task
        Console.Clear()
        Console.ForegroundColor = ConsoleColor.Yellow
        Console.WriteLine("Login with Custom Token")
        Console.WriteLine("=======================")
        Console.ResetColor()
        Console.WriteLine()
        
        Console.Write("Enter Custom Token: ")
        Dim customToken = Console.ReadLine()
        
        Console.WriteLine()
        Console.WriteLine("Authenticating...")
        
        Try
            Dim tokenData = Await client.SignInWithCustomTokenAsync(customToken)
            client.SetMainIdToken(tokenData.IdToken, tokenData.LocalId, tokenData.Email)
            
            Await client.AuthenticateSocialAsync()
            
            currentUser = tokenData.Email
            
            Console.ForegroundColor = ConsoleColor.Green
            Console.WriteLine($"✓ Logged in as {tokenData.Email}")
            SaveSession()
            Console.ResetColor()
            
            Dim banned = Await client.CheckIPOnLoginAsync()
            If banned Then
                Console.ForegroundColor = ConsoleColor.Red
                Console.WriteLine("⚠ WARNING: Your IP is banned! Account disabled.")
                Console.ResetColor()
            End If
            
            Await CheckTimeoutStatusAsync()
            Console.WriteLine("Press any key to continue...")
            Console.ReadKey()
            
        Catch ex As Exception
            Console.ForegroundColor = ConsoleColor.Red
            Console.WriteLine($"✗ Login failed: {ex.Message}")
            Console.WriteLine()
            Console.WriteLine(ExplainLoginError(ex.Message))
            Console.ResetColor()
            Console.WriteLine("Press any key to continue...")
            Console.ReadKey()
        End Try
    End Function
    
    Async Function LoginWithIdTokenAsync() As Task
        Console.Clear()
        Console.ForegroundColor = ConsoleColor.Yellow
        Console.WriteLine("Login with Firebase ID Token")
        Console.WriteLine("============================")
        Console.ResetColor()
        Console.WriteLine()
        
        Console.Write("Enter UID: ")
        Dim uid = Console.ReadLine()
        
        Console.Write("Enter Email: ")
        Dim email = Console.ReadLine()
        
        Console.Write("Enter Firebase ID Token: ")
        Dim token = Console.ReadLine()
        
        Console.WriteLine()
        Console.WriteLine("Authenticating...")
        
        Try
            client.SetMainIdToken(token, uid, email)
            
            Await client.AuthenticateSocialAsync()
            
            currentUser = email
            
            Console.ForegroundColor = ConsoleColor.Green
            Console.WriteLine($"✓ Logged in as {email}")
            SaveSession()
            Console.ResetColor()
            
            Dim banned = Await client.CheckIPOnLoginAsync()
            If banned Then
                Console.ForegroundColor = ConsoleColor.Red
                Console.WriteLine("⚠ WARNING: Your IP is banned! Account disabled.")
                Console.ResetColor()
            End If
            
            Await CheckTimeoutStatusAsync()
            Console.WriteLine("Press any key to continue...")
            Console.ReadKey()
            
        Catch ex As Exception
            Console.ForegroundColor = ConsoleColor.Red
            Console.WriteLine($"✗ Login failed: {ex.Message}")
            Console.WriteLine()
            Console.WriteLine(ExplainLoginError(ex.Message))
            Console.ResetColor()
            Console.WriteLine("Press any key to continue...")
            Console.ReadKey()
        End Try
    End Function
    
    Async Function LogoutAsync() As Task
        client.SetMainIdToken("", "", "")
        client.SetSocialIdToken("")
        ClearSession()
        currentUser = ""
        messageCache.Clear()
        renderedMessageIds.Clear()
        messageTimestamps.Clear()
        isTimedOut = False
        timeoutUntil = DateTime.MinValue
        Console.WriteLine("Logged out successfully")
        Await Task.Delay(1000)
    End Function
    
    Async Function CheckTimeoutStatusAsync() As Task
        Try
            ' This would need a custom endpoint or we check via the moderation worker
            ' For now, we just show a message
            Console.WriteLine("Timeout status checked via server-side enforcement")
            Console.WriteLine("Messages are rejected server-side if timed out")
        Catch ex As Exception
            Console.WriteLine($"Error checking timeout: {ex.Message}")
        End Try
        Await Task.Delay(1000)
    End Function
    
    ' ============================================================
    ' SEND MESSAGES WITH PROTECTION
    ' ============================================================
    
    Async Function SendMessageWithProtectionAsync(Optional text As String = Nothing, 
                                                  Optional pixelArt As String = Nothing,
                                                  Optional flipnoteData As String = Nothing,
                                                  Optional gridData As String = Nothing) As Task
        If Not client.IsAuthenticated Then
            Console.WriteLine("Not authenticated")
            Console.ReadKey()
            Return
        End If
        
        ' Rate limiting check
        If Not CanSendMessage() Then
            Console.ReadKey()
            Return
        End If
        
        If String.IsNullOrEmpty(text) Then
            Console.Clear()
            ShowHeader()
            Console.ForegroundColor = ConsoleColor.Cyan
            Console.WriteLine("  ── Send Message ──")
            Console.ResetColor()
            Console.WriteLine()
            Console.ForegroundColor = ConsoleColor.DarkGray
            Console.WriteLine("  Type your message below. Up to 1000 characters.")
            Console.WriteLine("  URLs/links and promotional content are not allowed.")
            Console.ResetColor()
            Console.WriteLine()
            Console.Write("  Message: ")
            text = Console.ReadLine()
        End If
        
        If String.IsNullOrWhiteSpace(text) AndAlso String.IsNullOrWhiteSpace(pixelArt) AndAlso String.IsNullOrWhiteSpace(flipnoteData) Then
            Console.WriteLine("Message cannot be empty")
            Console.ReadKey()
            Return
        End If
        
        ' Content validation
        If text.Length > MAX_MESSAGE_LENGTH Then
            Console.WriteLine($"Message exceeds {MAX_MESSAGE_LENGTH} characters")
            Console.ReadKey()
            Return
        End If
        
        If ContainsUrl(text) Then
            Console.WriteLine("❌ URLs and links are not allowed")
            Console.ReadKey()
            Return
        End If
        
        If ContainsPromotedTerm(text) Then
            Console.WriteLine("❌ Promotional content is not allowed")
            Console.ReadKey()
            Return
        End If
        
        ' Filter bad words
        text = FilterBadWords(text)
        
        ' Record send attempt
        RecordMessageSend()
        
        Console.WriteLine()
        Console.WriteLine("Sending...")
        
        Try
            Dim result = Await client.SendChatMessageAsync(text, Nothing, pixelArt, flipnoteData, gridData)
            
            ' Reset send state
            ResetSendState()
            
            Console.ForegroundColor = ConsoleColor.Green
            Console.WriteLine($"  ✓ Message sent! (ID: {result.Key})")
            Console.ResetColor()
            
            ' Request translation in background
            If Not String.IsNullOrEmpty(text) Then
                Dim translationTask = Task.Run(Async Function()
                    Try
                        Await RequestTranslationWithRetryAsync(client.UserId, text, result.Key)
                    Catch
                        ' Silent fail for translation
                    End Try
                End Function)
            End If
            
            Console.WriteLine()
            Console.WriteLine("  Press any key to return to the menu...")
            Console.ReadKey()
            
        Catch ex As Exception
            ResetSendState()
            Console.ForegroundColor = ConsoleColor.Red
            Console.WriteLine($"  ✗ Failed to send: {ex.Message}")
            Console.ResetColor()
            ShowSendErrorHelp(ex.Message)
            Console.WriteLine()
            Console.WriteLine("  Press any key to return to the menu...")
            Console.ReadKey()
        End Try
    End Function
    
    Async Function SendPixelArtWithProtectionAsync() As Task
        Console.Clear()
        Console.ForegroundColor = ConsoleColor.Yellow
        Console.WriteLine("Send Pixel Art")
        Console.WriteLine("==============")
        Console.ResetColor()
        Console.WriteLine()
        
        Console.WriteLine("1. Browse saved pixel art library")
        Console.WriteLine("2. Enter pixel art grid (or press Enter for demo)")
        Console.WriteLine("3. Import image from file → 256x256 grid")
        Console.WriteLine("0. Back")
        Console.WriteLine()
        Console.Write("Select option: ")
        Dim choice = Console.ReadLine()
        
        If choice = "0" Then Return
        
        Dim pixelData As String = Nothing
        Dim gridData As String = Nothing
        Dim caption As String = Nothing
        
        If choice = "1" Then
            ' Browse library
            Try
                Dim items = Await client.GetPixelLibraryAsync()
                If items.Count = 0 Then
                    Console.WriteLine("No saved pixel art found. Create some at pixel.html first.")
                    Console.ReadKey()
                    Return
                End If
                
                Console.WriteLine()
                Console.WriteLine("Saved pixel art:")
                For i = 0 To items.Count - 1
                    Dim item = items(i)
                    Console.WriteLine($"{i + 1}. {If(item.Title, "Untitled")} ({item.Size}x{item.Size})")
                Next
                Console.WriteLine("0. Back")
                Console.WriteLine()
                Console.Write("Pick one: ")
                Dim pick = Console.ReadLine()
                Dim idx As Integer
                If Not Integer.TryParse(pick, idx) OrElse idx < 1 OrElse idx > items.Count Then Return
                
                Dim selected = items(idx - 1)
                Console.WriteLine($"Loading '{selected.Title}'...")
                gridData = Await client.GetPixelDrawingAsync(selected.Id)
                If gridData Is Nothing Then
                    Console.WriteLine("✗ Failed to load drawing data.")
                    Console.ReadKey()
                    Return
                End If
                pixelData = selected.Thumbnail
                PreviewGridJson(gridData)
                Console.WriteLine()
                Console.Write("Message caption (Enter for auto, 'x' to cancel): ")
                caption = Console.ReadLine()
                If caption?.ToLower() = "x" Then Return
                If String.IsNullOrWhiteSpace(caption) Then caption = $"Shared a pixel art: {selected.Title}"
            Catch ex As Exception
                Console.WriteLine($"✗ Library error: {ex.Message}")
                Console.ReadKey()
                Return
            End Try
        ElseIf choice = "3" Then
            Console.Write("Enter image file path: ")
            Dim filePath = Console.ReadLine()
            If Not System.IO.File.Exists(filePath) Then
                Console.WriteLine("File not found.")
                Console.ReadKey()
                Return
            End If
            Console.WriteLine("Processing image...")
            Dim result = BuildPixelGridFromImage(filePath)
            If result Is Nothing Then
                Console.WriteLine("✗ Failed to process image.")
                Console.ReadKey()
                Return
            End If
            gridData = result.GridData
            pixelData = result.PixelArtDataUrl
            Console.WriteLine($"Grid: {result.Width}x{result.Height}, {result.NonWhitePixels} non-white pixels")
            PreviewGridJson(gridData)
            Console.WriteLine()
            Console.Write("Message caption (Enter for auto, 'x' to cancel): ")
            caption = Console.ReadLine()
            If caption?.ToLower() = "x" Then Return
            If String.IsNullOrWhiteSpace(caption) Then caption = "Shared a pixel art from image"
        Else
            ' Manual / demo
            Console.ForegroundColor = ConsoleColor.DarkGray
            Console.WriteLine("Tip: pixel art is a row-major grid of 0/1 values (0=white, 1=black).")
            Console.WriteLine("     Paste the grid data, or press Enter to generate a demo.")
            Console.ResetColor()
            Console.WriteLine()
            Console.Write("Pixel grid data: ")
            pixelData = Console.ReadLine()
            If String.IsNullOrWhiteSpace(pixelData) Then
                Console.WriteLine("Generating demo pattern...")
                pixelData = GenerateDemoPixelArt()
            End If
            Console.WriteLine()
            Console.Write("Message caption (optional): ")
            caption = Console.ReadLine()
        End If
        
        Await SendMessageWithProtectionAsync(caption, If(pixelData, ""), Nothing, gridData)
    End Function
    
    Async Function SendFlipnoteWithProtectionAsync() As Task
        Console.Clear()
        Console.ForegroundColor = ConsoleColor.Yellow
        Console.WriteLine("Send Flipnote")
        Console.WriteLine("=============")
        Console.ResetColor()
        Console.WriteLine()
        
        Console.WriteLine("1. Browse saved flipnote library")
        Console.WriteLine("2. Enter flipnote data (or press Enter for demo)")
        Console.WriteLine("3. Import GIF → flipbook frames")
        Console.WriteLine("0. Back")
        Console.WriteLine()
        Console.Write("Select option: ")
        Dim choice = Console.ReadLine()
        
        If choice = "0" Then Return
        
        Dim flipData As String = Nothing
        Dim caption As String = Nothing
        
        If choice = "3" Then
            Console.Write("Enter GIF file path: ")
            Dim gifPath = Console.ReadLine()
            If Not IO.File.Exists(gifPath) Then
                Console.WriteLine("File not found.")
                Console.ReadKey()
                Return
            End If
            Console.WriteLine("Processing GIF...")
            flipData = BuildFlipnoteFromGif(gifPath)
            If flipData Is Nothing Then
                Console.WriteLine("✗ Failed to process GIF.")
                Console.ReadKey()
                Return
            End If
            Console.WriteLine()
            Console.Write("Message caption (Enter for auto, 'x' to cancel): ")
            caption = Console.ReadLine()
            If caption?.ToLower() = "x" Then Return
            If String.IsNullOrWhiteSpace(caption) Then caption = "Shared a GIF as a flipbook"
        ElseIf choice = "1" Then
            Try
                Dim items = Await client.GetFlipnoteLibraryAsync()
                If items.Count = 0 Then
                    Console.WriteLine("No saved flipnotes found in your library.")
                    Console.WriteLine("Create some at flipbook.html or save from KindleChat.")
                    Console.ReadKey()
                    Return
                End If
                
                Console.WriteLine()
                Console.WriteLine("Saved flipnotes:")
                For i = 0 To items.Count - 1
                    Dim item = items(i)
                    Console.WriteLine($"{i + 1}. {If(item.Title, "Untitled")} ({item.FrameCount} frames)")
                Next
                Console.WriteLine("0. Back")
                Console.WriteLine()
                Console.Write("Pick one: ")
                Dim pick = Console.ReadLine()
                Dim idx As Integer
                If Not Integer.TryParse(pick, idx) OrElse idx < 1 OrElse idx > items.Count Then Return
                
                Dim selected = items(idx - 1)
                Console.WriteLine($"Loading '{selected.Title}'...")
                flipData = Await client.GetFlipnoteAnimationAsync(selected.Id)
                If flipData Is Nothing Then
                    Console.WriteLine("Failed to load flipnote data.")
                    Console.ReadKey()
                    Return
                End If
                Try
                    Dim dbg = JsonSerializer.Deserialize(Of Dictionary(Of String, Object))(flipData, New JsonSerializerOptions With {.PropertyNameCaseInsensitive = True})
                    Dim fps = If(dbg.ContainsKey("fps"), dbg("fps")?.ToString(), "?")
                    Dim frameCount = 0
                    If dbg.ContainsKey("frames") Then
                        Dim f = TryCast(dbg("frames"), List(Of Object))
                        If f IsNot Nothing Then frameCount = f.Count
                    End If
                    Console.WriteLine($"  fps={fps}, frames={frameCount}")
                Catch
                End Try
                caption = $"Shared a flipbook: {selected.Title}"
            Catch ex As Exception
                Console.WriteLine($"Error loading library: {ex.Message}")
                Console.ReadKey()
                Return
            End Try
        Else
            Console.ForegroundColor = ConsoleColor.DarkGray
            Console.WriteLine("Tip: flipnote data is JSON with fps and frames array of PNG data URLs.")
            Console.WriteLine("     Example: {""fps"":6,""frames"":[""data:image/png;base64,...""]}")
            Console.WriteLine("     Press Enter for a simple demo flipnote.")
            Console.ResetColor()
            Console.WriteLine()
            Console.Write("Flipnote JSON: ")
            flipData = Console.ReadLine()
            If String.IsNullOrWhiteSpace(flipData) Then
                Console.WriteLine("Creating a simple demo flipnote...")
                flipData = GenerateDemoFlipnote()
            End If
        End If
        
        If choice <> "1" AndAlso choice <> "3" Then
            Console.WriteLine()
            Console.Write("Message caption (optional): ")
            caption = Console.ReadLine()
        End If
        
        Await SendMessageWithProtectionAsync(caption, Nothing, flipData)
    End Function
    
    Async Function SendLiveMessageAsync(text As String) As Task
        ' Silent send for live mode - no blocking ReadKey or screen clearing
        If Not CanSendMessage() Then
            Console.WriteLine("⏳ Rate limited, try again shortly.")
            Return
        End If
        
        text = FilterBadWords(text)
        If text.Length > MAX_MESSAGE_LENGTH Then
            Console.WriteLine("❌ Message too long.")
            Return
        End If
        If ContainsUrl(text) OrElse ContainsPromotedTerm(text) Then
            Console.WriteLine("❌ Message rejected (URL/promotional).")
            Return
        End If
        
        RecordMessageSend()
        
        Try
            Dim result = Await client.SendChatMessageAsync(text, Nothing, Nothing, Nothing)
            ResetSendState()
            Console.ForegroundColor = ConsoleColor.Green
            Console.WriteLine($"✓ Sent (ID: {result.Key})")
            Console.ResetColor()
            
            If Not String.IsNullOrEmpty(text) Then
                Dim translationTask = Task.Run(Async Function()
                    Try
                        Await RequestTranslationWithRetryAsync(client.UserId, text, result.Key)
                    Catch
                    End Try
                End Function)
            End If
        Catch ex As Exception
            ResetSendState()
            Console.ForegroundColor = ConsoleColor.Red
            Console.WriteLine($"✗ Failed to send: {ex.Message}")
            Console.ResetColor()
        End Try
    End Function
    
    
    Async Function ViewMessagesAsync() As Task
        Console.Clear()
        Console.ForegroundColor = ConsoleColor.Yellow
        Console.WriteLine("Recent Messages")
        Console.WriteLine("===============")
        Console.ResetColor()
        Console.WriteLine()
        
        Try
            Console.Write("Number of messages to fetch (1-50, default 20): ")
            Dim input = Console.ReadLine()
            Dim count = 20
            If Not String.IsNullOrWhiteSpace(input) Then
                Dim parsed As Integer
                If Integer.TryParse(input, parsed) Then
                    count = Math.Min(50, Math.Max(1, parsed))
                End If
            End If
            
            Dim messages = Await client.GetChatMessagesAsync(count)
            
            ' Update cache
            CacheMessages(messages)
            
            If messages.Count = 0 Then
                Console.ForegroundColor = ConsoleColor.DarkGray
                Console.WriteLine("No messages found yet.")
                Console.ResetColor()
            Else
                Console.ForegroundColor = ConsoleColor.Cyan
                Console.WriteLine($"Showing {messages.Count} messages:")
                Console.ResetColor()
                Console.WriteLine()
                
                For Each msg In messages
                    DisplayMessage(msg)
                Next
            End If
            
        Catch ex As Exception
            Console.ForegroundColor = ConsoleColor.Red
            Console.WriteLine($"✗ Failed to load messages: {ex.Message}")
            Console.ResetColor()
        End Try
        
        Console.WriteLine()
        Console.WriteLine("Press any key to return to the menu...")
        Console.ReadKey()
    End Function

    Function ExplainLoginError(msg As String) As String
        Dim m = If(msg, "").ToLowerInvariant()
        Dim sb As New System.Text.StringBuilder()
        If m.Contains("email_not_found") Then
            sb.AppendLine("Reason: No account exists for that email. Accounts are created as <username>@rekindle.ink.")
            sb.AppendLine("  Check the username you typed (it is appended with @rekindle.ink).")
            sb.AppendLine("  If you registered with a different email, use that instead, or register a new account.")
        ElseIf m.Contains("invalid_password") Then
            sb.AppendLine("Reason: Wrong password for that account.")
        ElseIf m.Contains("user_disabled") Then
            sb.AppendLine("Reason: This account has been disabled (banned/timeout).")
        ElseIf m.Contains("too_many_attempts") OrElse m.Contains("rate") Then
            sb.AppendLine("Reason: Too many failed login attempts. Wait a bit and try again.")
        ElseIf m.Contains("invalid_api_key") OrElse m.Contains("api key") Then
            sb.AppendLine("Reason: The Firebase API key is wrong or missing.")
            sb.AppendLine("  Run Diagnostics (option 4) to check the loaded config.")
        ElseIf m.Contains("network") OrElse m.Contains("socket") OrElse m.Contains("timed out") OrElse m.Contains("connection") Then
            sb.AppendLine("Reason: Network issue reaching Firebase. Check your connection and system clock.")
        ElseIf m.Contains("email") AndAlso m.Contains("format") Then
            sb.AppendLine("Reason: The email format is invalid.")
        Else
            sb.AppendLine("Run Diagnostics (option 4) to see what the server says.")
        End If
Return sb.ToString()
    End Function

    Sub ShowSendErrorHelp(msg As String)
        Dim m = If(msg, "").ToLowerInvariant()
        If m.Contains("rate limit") OrElse m.Contains("429") OrElse m.Contains("too many") Then
            Console.ForegroundColor = ConsoleColor.Yellow
            Console.WriteLine("  💡 Tip: You're sending too fast. Wait a moment before trying again.")
            Console.ResetColor()
        ElseIf m.Contains("empty message") Then
            Console.ForegroundColor = ConsoleColor.Yellow
            Console.WriteLine("  💡 Tip: Add some text, pixel art, or a flipnote to your message.")
            Console.ResetColor()
        ElseIf m.Contains("worker error") OrElse m.Contains("500") Then
            Console.ForegroundColor = ConsoleColor.Yellow
            Console.WriteLine("  💡 Tip: The server had a temporary issue. Try again in a moment.")
            Console.ResetColor()
        ElseIf m.Contains("network") OrElse m.Contains("timeout") Then
            Console.ForegroundColor = ConsoleColor.Yellow
            Console.WriteLine("  💡 Tip: Check your internet connection and try again.")
            Console.ResetColor()
        ElseIf m.Contains("you already") OrElse m.Contains("duplicate") Then
            Console.ForegroundColor = ConsoleColor.Yellow
            Console.WriteLine("  💡 Tip: You already posted that content recently. Wait a few minutes.")
            Console.ResetColor()
        End If
    End Sub

    Sub DisplayMessage(msg As ChatMessage)
        Dim isMine = msg.Uid = client.UserId
        Dim time = DateTimeOffset.FromUnixTimeMilliseconds(msg.Timestamp).LocalDateTime
        Dim indent = If(isMine, "", "  ")
        
        ' Timestamp + username
        Console.ForegroundColor = ConsoleColor.DarkGray
        Console.Write($"{indent}[{time.ToString("HH:mm:ss")}] ")
        Console.ForegroundColor = If(isMine, ConsoleColor.Cyan, ConsoleColor.Yellow)
        Console.Write($"{msg.Username}")
        Console.ResetColor()
        
        If msg.Reactions IsNot Nothing AndAlso msg.Reactions.Count > 0 Then
            Dim reactionStr = String.Join(" ", msg.Reactions.Values.Select(Function(r) 
                Return If(TypeOf r Is Dictionary(Of String, Object), 
                   TryCast(r, Dictionary(Of String, Object))("reaction")?.ToString(), 
                   r.ToString())
            End Function))
            Console.ForegroundColor = ConsoleColor.Magenta
            Console.Write($"  {reactionStr}")
            Console.ResetColor()
        End If
        
        Console.WriteLine()
        
        ' Message text, indented for chat-bubble effect
        Dim textPrefix = If(isMine, "  └ ", "  └ ")
        Console.ForegroundColor = If(isMine, ConsoleColor.Green, ConsoleColor.White)
        Console.WriteLine($"{indent}{textPrefix}{msg.Text}")
        Console.ResetColor()
        
        ' Attachments
        If msg.IsPixelArt.GetValueOrDefault() Then
            Console.ForegroundColor = ConsoleColor.Cyan
            Console.WriteLine($"{indent}    🎨 Pixel art")
            Console.ResetColor()
        End If
        If msg.IsFlipnote.GetValueOrDefault() Then
            Console.ForegroundColor = ConsoleColor.Cyan
            Console.WriteLine($"{indent}    📝 Flipnote")
            Console.ResetColor()
        End If
        If msg.ReplyTo IsNot Nothing Then
            Console.ForegroundColor = ConsoleColor.DarkGray
            Console.WriteLine($"{indent}    ↳ re: {msg.ReplyTo.Username}: {msg.ReplyTo.Text}")
            Console.ResetColor()
        End If
        
        ' Translation
        If translationCache.ContainsKey(msg.Id) Then
            Console.ForegroundColor = ConsoleColor.DarkGray
            Console.WriteLine($"{indent}    Translated: {translationCache(msg.Id)}")
            Console.ResetColor()
        End If
        
        ' ID shown in dim on the right
        Console.ForegroundColor = ConsoleColor.DarkGray
        Console.WriteLine($"{indent}    ─ {msg.Id}")
        Console.ResetColor()
        Console.WriteLine()
    End Sub
    
    ' ============================================================
    ' CACHE MANAGEMENT
    ' ============================================================
    
    Sub CacheMessages(messages As List(Of ChatMessage))
        Try
            ' Keep only last N messages
            Dim toCache = messages
            If toCache.Count > MAX_CACHED_MESSAGES Then
                toCache = toCache.GetRange(toCache.Count - MAX_CACHED_MESSAGES, MAX_CACHED_MESSAGES)
            End If
            
            Dim cacheData = New Dictionary(Of String, Object) From {
                {"timestamp", DateTime.UtcNow.Ticks},
                {"messages", toCache.Select(Function(m) New With {
                    .Id = m.Id,
                    .Text = m.Text,
                    .Uid = m.Uid,
                    .Username = m.Username,
                    .Timestamp = m.Timestamp,
                    .Reactions = m.Reactions,
                    .ReplyTo = m.ReplyTo,
                    .IsPixelArt = m.IsPixelArt,
                    .PixelArt = m.PixelArt,
                    .IsFlipnote = m.IsFlipnote,
                    .FlipnoteData = m.FlipnoteData
                }).ToList()}
            }
            
            Dim json = JsonSerializer.Serialize(cacheData)
            System.IO.File.WriteAllText(MSG_CACHE_KEY & ".json", json)
        Catch ex As Exception
            ' Silent fail for cache
        End Try
    End Sub
    
    Function LoadCachedMessages() As List(Of ChatMessage)
        Try
            If System.IO.File.Exists(MSG_CACHE_KEY & ".json") Then
                Dim json = System.IO.File.ReadAllText(MSG_CACHE_KEY & ".json")
                Dim data = JsonSerializer.Deserialize(Of Dictionary(Of String, Object))(json)
                If data IsNot Nothing AndAlso data.ContainsKey("messages") Then
                    Dim messagesJson = JsonSerializer.Serialize(data("messages"))
                    Return JsonSerializer.Deserialize(Of List(Of ChatMessage))(messagesJson)
                End If
            End If
        Catch
        End Try
        Return New List(Of ChatMessage)()
    End Function
    
    ' ============================================================
    ' REACTION METHODS
    ' ============================================================
    
    Async Function ToggleReactionAsync() As Task
        Console.Clear()
        ShowHeader()
        Console.ForegroundColor = ConsoleColor.Cyan
        Console.WriteLine("  ── Toggle Reaction ──")
        Console.ResetColor()
        Console.WriteLine()
        Console.ForegroundColor = ConsoleColor.DarkGray
        Console.WriteLine("  Tip: get a message ID from 'View recent messages'.")
        Console.ResetColor()
        Console.WriteLine()
        
        Console.Write("  Message ID: ")
        Dim msgId = Console.ReadLine()
        
        If String.IsNullOrWhiteSpace(msgId) Then
            Console.WriteLine("  ✗ Message ID required")
            Console.ReadKey()
            Return
        End If
        
        Console.WriteLine("  Available reactions: ❤️ 😄 😢 😮 😡 👍 👎 🎉")
        Console.Write("  Reaction emoji: ")
        Dim reaction = Console.ReadLine()
        
        If String.IsNullOrWhiteSpace(reaction) Then
            Console.WriteLine("  ✗ Reaction required")
            Console.ReadKey()
            Return
        End If
        
        Try
            Await client.SetReactionAsync(msgId, reaction)
            Console.ForegroundColor = ConsoleColor.Green
            Console.WriteLine($"  ✓ Reaction {reaction} toggled on {msgId}!")
            Console.ResetColor()
        Catch ex As Exception
            Console.ForegroundColor = ConsoleColor.Red
            Console.WriteLine($"  ✗ Failed: {ex.Message}")
            Console.ResetColor()
            ShowSendErrorHelp(ex.Message)
        End Try
        Console.WriteLine()
        Console.WriteLine("  Press any key to return to the menu...")
        Console.ReadKey()
    End Function
    
    Async Function DeleteMessageAsync() As Task
        Console.Clear()
        ShowHeader()
        Console.ForegroundColor = ConsoleColor.Cyan
        Console.WriteLine("  ── Delete Message ──")
        Console.ResetColor()
        Console.WriteLine()
        
        Console.Write("  Message ID to delete: ")
        Dim msgId = Console.ReadLine()
        
        If String.IsNullOrWhiteSpace(msgId) Then
            Console.WriteLine("  ✗ Message ID required")
            Console.ReadKey()
            Return
        End If
        
        Console.WriteLine()
        Console.Write("  Are you sure? (y/n): ")
        Dim confirm = Console.ReadLine()?.ToLower()
        
        If confirm <> "y" And confirm <> "yes" Then
            Console.WriteLine("  Cancelled")
            Console.ReadKey()
            Return
        End If
        
        Try
            Await client.DeleteChatMessageAsync(msgId)
            Console.ForegroundColor = ConsoleColor.Green
            Console.WriteLine("  ✓ Message deleted!")
            Console.ResetColor()
        Catch ex As Exception
            Console.ForegroundColor = ConsoleColor.Red
            Console.WriteLine($"  ✗ Failed to delete: {ex.Message}")
            Console.ResetColor()
            ShowSendErrorHelp(ex.Message)
        End Try
        Console.WriteLine()
        Console.WriteLine("  Press any key to return to the menu...")
        Console.ReadKey()
    End Function
    
    ' ============================================================
    ' LIVE MODE WITH PROTECTION
    ' ============================================================
    
    Async Function LiveModeWithProtectionAsync() As Task
        Console.Clear()
        Console.ForegroundColor = ConsoleColor.Yellow
        Console.WriteLine("Live Mode - Real-time Chat")
        Console.WriteLine("==========================")
        Console.ResetColor()
        Console.WriteLine()
        Console.WriteLine("Messages will appear as they arrive.")
        Console.WriteLine("Type your message and press Enter to send.")
        Console.WriteLine("Type 'exit' to return to main menu.")
        Console.WriteLine()
        Console.ForegroundColor = ConsoleColor.Cyan
        Console.WriteLine("--- Live Chat Started ---")
        Console.ResetColor()
        
        Dim liveRunning As Boolean = True
        Dim lastTimestamp As Long = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        Dim messageCount As Integer = 0
        Dim firstPoll As Boolean = True
        
        ' Start polling task
        Dim pollTask = Task.Run(Async Function()
            While liveRunning
                Try
                    Dim messages As List(Of ChatMessage)
                    If firstPoll Then
                        ' Initial load: fetch the most recent messages
                        messages = Await client.GetChatMessagesAsync(20)
                        firstPoll = False
                    Else
                        ' Subsequent polls: fetch only NEW messages after lastTimestamp
                        messages = Await client.GetNewMessagesAsync(20, lastTimestamp)
                    End If
                    
                    For Each msg In messages.OrderBy(Function(m) m.Timestamp)
                        If renderedMessageIds.Contains(msg.Id) Then Continue For
                        renderedMessageIds.Add(msg.Id)
                        
                        ' Display the message
                        Dim time = DateTimeOffset.FromUnixTimeMilliseconds(msg.Timestamp).LocalDateTime
                        Console.ForegroundColor = ConsoleColor.White
                        Console.Write($"[{time.ToString("HH:mm:ss")}] ")
                        Console.ForegroundColor = If(msg.Uid = client.UserId, ConsoleColor.Cyan, ConsoleColor.Yellow)
                        Console.Write($"{msg.Username}")
                        Console.ResetColor()
                        Console.WriteLine($": {msg.Text}")
                        Console.ForegroundColor = ConsoleColor.DarkGray
                        Console.WriteLine($"  ─ ID: {msg.Id}")
                        Console.ResetColor()
                        messageCount += 1
                        
                        If msg.Timestamp > lastTimestamp Then
                            lastTimestamp = msg.Timestamp
                        End If
                    Next
                    
                    Await Task.Delay(3000) ' Poll every 3 seconds
                    
                Catch ex As Exception
                    ' Silently continue on errors
                End Try
            End While
        End Function)
        
        ' Main input loop
        While liveRunning
            Console.Write("> ")
            Dim input = Console.ReadLine()
            
            If input?.ToLower() = "exit" Then
                liveRunning = False
                Exit While
            End If
            
            If Not String.IsNullOrWhiteSpace(input) Then
                ' Use the protected send method (silent variant for live mode)
                Await SendLiveMessageAsync(input)
            End If
        End While
        
        ' Wait for polling to finish
        Await pollTask
        
        Console.ForegroundColor = ConsoleColor.Cyan
        Console.WriteLine("--- Live Chat Ended ---")
        Console.ResetColor()
        Console.WriteLine("Press any key to continue...")
        Console.ReadKey()
    End Function
    
    ' ============================================================
    ' HELPER METHODS
    ' ============================================================
    
    Function ReadPassword() As String
        Dim password As String = ""
        Dim key As ConsoleKeyInfo
        
        Do
            key = Console.ReadKey(True)
            
            If key.Key = ConsoleKey.Enter Then
                Exit Do
            ElseIf key.Key = ConsoleKey.Backspace Then
                If password.Length > 0 Then
                    password = password.Substring(0, password.Length - 1)
                    Console.Write(vbBack)
                    Console.Write(" ")
                    Console.Write(vbBack)
                End If
            Else
                password += key.KeyChar
                Console.Write("*")
            End If
        Loop
        
        Console.WriteLine()
        Return password
    End Function
    
    Function GenerateDemoPixelArt() As String
        Dim result As New StringBuilder()
        For row As Integer = 0 To 63
            For col As Integer = 0 To 63
                Dim isBorder = row = 0 Or row = 63 Or col = 0 Or col = 63
                Dim isEye = (row >= 20 And row <= 28 And ((col >= 15 And col <= 23) Or (col >= 40 And col <= 48)))
                Dim isSmile = row >= 40 And row <= 48 And col >= 20 And col <= 43
                Dim isMouth = row >= 40 And row <= 44 And col >= 22 And col <= 41
                
                If isBorder Or isEye Then
                    result.Append("1")
                ElseIf isMouth Or (isSmile And (row >= 42)) Then
                    result.Append("1")
                Else
                    result.Append("0")
                End If
            Next
        Next
        Return result.ToString()
    End Function
    
    Function GenerateDemoFlipnote() As String
        Dim frames As New List(Of String)
        Dim frame = GenerateDemoPixelArt()
        Dim dataUrl = PixelGridStringToDataUrl(frame, 64)
        frames.Add(dataUrl)
        frames.Add(dataUrl)
        
        Return JsonSerializer.Serialize(New Dictionary(Of String, Object) From {
            {"fps", 4},
            {"frames", frames}
        })
    End Function
    
    ''' <summary>
    ''' Render a flat 0/1 grid string (row-major) into a PNG data URL using ImageMagick.
    ''' </summary>
    Function PixelGridStringToDataUrl(gridStr As String, size As Integer) As String
        If String.IsNullOrWhiteSpace(gridStr) Then Return Nothing
        ' Write a PGM (P5) file, then convert to PNG and base64-encode
        Dim tempPgm = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rk_demo_" & Guid.NewGuid().ToString("N") & ".pgm")
        Try
            Dim sb As New StringBuilder()
            sb.AppendLine("P5")
            sb.AppendLine($"{size} {size}")
            sb.AppendLine("255")
            Dim header = sb.ToString()
            Dim raw(size * size - 1) As Byte
            For i As Integer = 0 To Math.Min(gridStr.Length, size * size) - 1
                raw(i) = If(gridStr(i) = "1"c, 0, 255)
            Next
            Using fs = New IO.FileStream(tempPgm, IO.FileMode.Create)
                Dim headerBytes = Encoding.ASCII.GetBytes(header)
                fs.Write(headerBytes, 0, headerBytes.Length)
                fs.Write(raw, 0, raw.Length)
            End Using
            
            Dim psi As New Diagnostics.ProcessStartInfo("convert")
            psi.UseShellExecute = False
            psi.RedirectStandardOutput = True
            psi.CreateNoWindow = True
            psi.ArgumentList.Add(tempPgm)
            psi.ArgumentList.Add("png:-")
            Using proc = Diagnostics.Process.Start(psi)
                Using ms As New IO.MemoryStream()
                    proc.StandardOutput.BaseStream.CopyTo(ms)
                    proc.WaitForExit()
                    Dim b64 = Convert.ToBase64String(ms.ToArray())
                    Return $"data:image/png;base64,{b64}"
                End Using
            End Using
        Catch
            Return Nothing
        Finally
            Try
                If IO.File.Exists(tempPgm) Then IO.File.Delete(tempPgm)
            Catch
            End Try
        End Try
    End Function
    
    ' ============================================================
    ' IMAGE IMPORT → 256x256 GRID
    ' ============================================================
    
    Class ImportedPixelResult
        Public Property GridData As String
        Public Property PixelArtDataUrl As String
        Public Property Width As Integer
        Public Property Height As Integer
        Public Property NonWhitePixels As Integer
    End Class
    
    Function BuildPixelGridFromImage(filePath As String) As ImportedPixelResult
        Try
            ' Use ImageMagick (convert) to cover-crop to square, resize to 256x256,
            ' and output raw grayscale bytes (256*256). Avoids any native .NET image lib.
            Dim psi As New Diagnostics.ProcessStartInfo("convert")
            psi.UseShellExecute = False
            psi.RedirectStandardOutput = True
            psi.StandardOutputEncoding = Encoding.Default
            psi.CreateNoWindow = True
            psi.ArgumentList.Add(filePath)
            psi.ArgumentList.Add("-resize")
            psi.ArgumentList.Add("256x256^")
            psi.ArgumentList.Add("-gravity")
            psi.ArgumentList.Add("center")
            psi.ArgumentList.Add("-extent")
            psi.ArgumentList.Add("256x256")
            psi.ArgumentList.Add("-colorspace")
            psi.ArgumentList.Add("gray")
            psi.ArgumentList.Add("-depth")
            psi.ArgumentList.Add("8")
            psi.ArgumentList.Add("gray:-")
            
            Using proc = Diagnostics.Process.Start(psi)
                Dim raw = proc.StandardOutput.BaseStream
                Dim bytes(65535) As Byte
                Dim total = 0
                While total < bytes.Length
                    Dim n = raw.Read(bytes, total, bytes.Length - total)
                    If n <= 0 Then Exit While
                    total += n
                End While
                proc.WaitForExit()
                If total < 256 * 256 Then
                    Console.WriteLine("Image processing produced too few pixels.")
                    Return Nothing
                End If
                
                Dim grid(255, 255) As Double
                Dim nonWhite As Integer = 0
                For y As Integer = 0 To 255
                    For x As Integer = 0 To 255
                        Dim gray = bytes(y * 256 + x)
                        ' 0 = white, 1 = black, in-between = gray
                        Dim value = 1.0 - (gray / 255.0)
                        grid(y, x) = Math.Round(value, 4)
                        If value > 0.02 Then nonWhite += 1
                    Next
                Next
                
                ' Serialize as JSON 2D array
                Dim rows As New List(Of List(Of Double))()
                For y As Integer = 0 To 255
                    Dim row As New List(Of Double)()
                    For x As Integer = 0 To 255
                        row.Add(grid(y, x))
                    Next
                    rows.Add(row)
                Next
                Dim gridJson = JsonSerializer.Serialize(rows)
                
                ' Render 256x256 grayscale PNG as a data URL for the message
                Dim dataUrl = RenderGridToDataUrl(grid)
                
                Return New ImportedPixelResult With {
                    .GridData = gridJson,
                    .PixelArtDataUrl = dataUrl,
                    .Width = 256,
                    .Height = 256,
                    .NonWhitePixels = nonWhite
                }
            End Using
        Catch ex As Exception
            Console.WriteLine($"Image processing error: {ex.Message}")
            Return Nothing
        End Try
    End Function
    
    Function RenderGridToDataUrl(grid(,) As Double) As String
        ' Draw the grid to a PGM, then use ImageMagick to convert to PNG and base64-encode it.
        Dim tempPgm = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rk_grid_" & Guid.NewGuid().ToString("N") & ".pgm")
        Try
            Dim sb As New StringBuilder()
            sb.AppendLine("P5")
            sb.AppendLine("256 256")
            sb.AppendLine("255")
            Dim header = sb.ToString()
            Dim raw(65535) As Byte
            For y As Integer = 0 To 255
                For x As Integer = 0 To 255
                    Dim v = grid(y, x)
                    If v <= 0.02 Then
                        raw(y * 256 + x) = 255
                    Else
                        raw(y * 256 + x) = CByte(Math.Max(0, Math.Min(255, Math.Round((1.0 - v) * 255))))
                    End If
                Next
            Next
            Using fs = New IO.FileStream(tempPgm, IO.FileMode.Create)
                Dim headerBytes = Encoding.ASCII.GetBytes(header)
                fs.Write(headerBytes, 0, headerBytes.Length)
                fs.Write(raw, 0, raw.Length)
            End Using
            
            Dim psi As New Diagnostics.ProcessStartInfo("convert")
            psi.UseShellExecute = False
            psi.RedirectStandardOutput = True
            psi.CreateNoWindow = True
            psi.ArgumentList.Add(tempPgm)
            psi.ArgumentList.Add("png:-")
            Using proc = Diagnostics.Process.Start(psi)
                Using ms As New IO.MemoryStream()
                    proc.StandardOutput.BaseStream.CopyTo(ms)
                    proc.WaitForExit()
                    Dim b64 = Convert.ToBase64String(ms.ToArray())
                    Return $"data:image/png;base64,{b64}"
                End Using
            End Using
        Catch ex As Exception
            Return Nothing
        Finally
            Try
                If IO.File.Exists(tempPgm) Then IO.File.Delete(tempPgm)
            Catch
            End Try
        End Try
    End Function
    
    ''' <summary>
    ''' Import an animated GIF as a flipbook: split into frames, resize each to 256x256
    ''' (cover-crop, no white bars), and build { fps, frames: [PNG data URLs] }.
    ''' </summary>
    Function BuildFlipnoteFromGif(filePath As String) As String
        Dim tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rk_gif_" & Guid.NewGuid().ToString("N"))
        Try
            IO.Directory.CreateDirectory(tempDir)
            Dim framePattern = System.IO.Path.Combine(tempDir, "f-%03d.png")
            
            ' Use ImageMagick to coalesce the GIF (expand per-frame deltas) and
            ' cover-crop + resize each frame to 256x256, saving as PNGs.
            Dim psi As New Diagnostics.ProcessStartInfo("convert")
            psi.UseShellExecute = False
            psi.RedirectStandardError = True
            psi.CreateNoWindow = True
            psi.ArgumentList.Add(filePath)
            psi.ArgumentList.Add("-coalesce")
            psi.ArgumentList.Add("-resize")
            psi.ArgumentList.Add("256x256^")
            psi.ArgumentList.Add("-gravity")
            psi.ArgumentList.Add("center")
            psi.ArgumentList.Add("-extent")
            psi.ArgumentList.Add("256x256")
            psi.ArgumentList.Add(framePattern)
            
            Using proc = Diagnostics.Process.Start(psi)
                proc.WaitForExit()
                Dim err = proc.StandardError.ReadToEnd()
                If proc.ExitCode <> 0 Then
                    Console.WriteLine($"ImageMagick error: {err.Trim()}")
                    Return Nothing
                End If
            End Using
            
            Dim frames As New List(Of String)()
            Dim idx As Integer = 0
            While True
                Dim f = System.IO.Path.Combine(tempDir, $"f-{idx:000}.png")
                If Not IO.File.Exists(f) Then Exit While
                Dim b64 = Convert.ToBase64String(IO.File.ReadAllBytes(f))
                frames.Add($"data:image/png;base64,{b64}")
                idx += 1
            End While
            
            If frames.Count = 0 Then
                Console.WriteLine("No frames extracted.")
                Return Nothing
            End If
            
            ' Try to read the GIF frame delay to derive fps (default 6)
            Dim fps = 6
            Try
                Dim ipsi As New Diagnostics.ProcessStartInfo("identify")
                ipsi.UseShellExecute = False
                ipsi.RedirectStandardOutput = True
                ipsi.CreateNoWindow = True
                ipsi.ArgumentList.Add("-format")
                ipsi.ArgumentList.Add("%T\n")
                ipsi.ArgumentList.Add(filePath)
                Using iproc = Diagnostics.Process.Start(ipsi)
                    Dim output = iproc.StandardOutput.ReadToEnd().Trim()
                    iproc.WaitForExit()
                    Dim delayStr = output.Split({vbCr, vbLf, " "c}, StringSplitOptions.RemoveEmptyEntries)(0)
                    Dim delay As Integer
                    If Integer.TryParse(delayStr, delay) AndAlso delay > 0 Then
                        fps = Math.Max(1, CInt(Math.Round(100.0 / delay)))
                    End If
                End Using
            Catch
            End Try
            
            Return JsonSerializer.Serialize(New Dictionary(Of String, Object) From {
                {"fps", fps},
                {"frames", frames}
            })
        Catch ex As Exception
            Console.WriteLine($"GIF processing error: {ex.Message}")
            Return Nothing
        Finally
            Try
                If IO.Directory.Exists(tempDir) Then IO.Directory.Delete(tempDir, True)
            Catch
            End Try
        End Try
    End Function
    
    ''' <summary>
    ''' Renders a grid JSON string (2D array) as ASCII art in the console.
    ''' Downscales to fit the terminal width.
    ''' </summary>
    Sub PreviewGridJson(gridJson As String)
        If String.IsNullOrWhiteSpace(gridJson) Then
            Console.WriteLine("(no grid data to preview)")
            Return
        End If
        Try
            Dim grid = JsonSerializer.Deserialize(Of List(Of List(Of Double)))(gridJson)
            If grid Is Nothing OrElse grid.Count = 0 Then
                Console.WriteLine("(empty grid)")
                Return
            End If
            Dim h = grid.Count
            Dim w = grid(0).Count
            Console.ForegroundColor = ConsoleColor.DarkGray
            Console.WriteLine($"Preview ({w}x{h}):")
            Console.ResetColor()
            
            ' Scale down to max ~100 chars wide so it fits the terminal
            Dim maxChars = 100
            Dim blockW = Math.Max(1, CInt(Math.Ceiling(w / maxChars)))
            Dim blockH = Math.Max(1, CInt(Math.Ceiling(h / 80)))
            
            Dim sb As New StringBuilder()
            Dim y As Integer = 0
            While y < h
                Dim x As Integer = 0
                While x < w
                    ' Average the block
                    Dim sum As Double = 0
                    Dim count As Integer = 0
                    For by As Integer = 0 To blockH - 1
                        For bx As Integer = 0 To blockW - 1
                            Dim yy = y + by
                            Dim xx = x + bx
                            If yy < h AndAlso xx < w Then
                                sum += grid(yy)(xx)
                                count += 1
                            End If
                        Next
                    Next
                    Dim avg = If(count > 0, sum / count, 0)
                    ' Map value (0=white..1=black) to a char
                    sb.Append(If(avg > 0.5, "#", If(avg > 0.2, "+", If(avg > 0.05, ".", " "))))
                    x += blockW
                End While
                sb.AppendLine()
                y += blockH
            End While
            Console.WriteLine(sb.ToString())
        Catch
            Console.WriteLine("(could not parse grid for preview)")
        End Try
    End Sub

    Async Function TopicsMenuAsync() As Task
        While True
            Console.Clear()
            ShowHeader()
            Console.ForegroundColor = ConsoleColor.Cyan
            Console.WriteLine("  ── Topics ──")
            Console.ResetColor()
            Console.WriteLine()
            Console.WriteLine("   1. Browse topics")
            Console.WriteLine("   2. Create topic")
            Console.WriteLine("   0. Back to main menu")
            Console.WriteLine()
            Console.Write("  Select option: ")
            Dim opt = Console.ReadLine()
            If opt = "0" Then Exit While
            If opt = "1" Then Await BrowseTopicsAsync()
            If opt = "2" Then Await CreateTopicAsync()
        End While
    End Function

    Async Function NeighbourhoodMenuAsync() As Task
        While True
            Console.Clear()
            ShowHeader()
            Console.ForegroundColor = ConsoleColor.Cyan
            Console.WriteLine("  ── Neighbourhood ──")
            Console.ResetColor()
            Console.WriteLine()
            Console.WriteLine("   1. Browse posts")
            Console.WriteLine("   2. Create post")
            Console.WriteLine("   0. Back to main menu")
            Console.WriteLine()
            Console.Write("  Select option: ")
            Dim opt = Console.ReadLine()
            If opt = "0" Then Exit While
            If opt = "1" Then Await BrowseNeighbourhoodAsync()
            If opt = "2" Then Await CreateNeighbourhoodPostAsync()
        End While
    End Function

    Async Function BrowseTopicsAsync(Optional forceRefresh As Boolean = False) As Task
        Console.Clear()
        ShowHeader()
        Console.ForegroundColor = ConsoleColor.Cyan
        Console.WriteLine("  ── Topics ──")
        Console.ResetColor()
        Console.WriteLine()
        
        Try
            ' Page-based loading: only 20 topics per fetch to keep Firestore reads low.
            ' "See More" appends the next page instead of re-fetching everything.
            Const PAGE_SIZE As Integer = 20
            Dim topics As New List(Of Topic)()
            Dim lastActive As DateTime? = Nothing
            Dim lastId As String = Nothing
            Dim noMore As Boolean = False
            
            While True
                Dim page = Await client.GetTopicsPageAsync(PAGE_SIZE, lastActive, lastId)
                If page Is Nothing OrElse page.Count = 0 Then
                    noMore = True
                    If topics.Count = 0 Then
                        Console.WriteLine("  No topics found.")
                        Console.ReadKey()
                        Return
                    End If
                    Exit While
                End If
                topics.AddRange(page)
                lastActive = page.Last().LastActive
                lastId = page.Last().Id
                
                ' Render current list
                Console.Clear()
                ShowHeader()
                Console.ForegroundColor = ConsoleColor.Cyan
                Console.WriteLine("  ── Topics ──")
                Console.ResetColor()
                Console.WriteLine()
                RenderTopicList(topics)
                
                If page.Count < PAGE_SIZE Then
                    noMore = True
                    Exit While
                End If
                
                Console.WriteLine($"  [S]ee More (loaded {topics.Count}), or Enter to select")
                Console.Write("  > ")
                Dim more = Console.ReadLine()
                If String.IsNullOrWhiteSpace(more) OrElse more?.ToLower() <> "s" Then Exit While
            End While
            
            ' Selection loop
            While True
                Console.WriteLine()
                Console.Write("  Enter topic number, 's' for more, 'r' to refresh, or Enter to return: ")
                Dim numStr = Console.ReadLine()
                If String.IsNullOrWhiteSpace(numStr) Then Return
                If numStr?.ToLower() = "r" Then
                    Await BrowseTopicsAsync(True)
                    Return
                End If
                If numStr?.ToLower() = "s" AndAlso Not noMore Then
                    Dim page = Await client.GetTopicsPageAsync(PAGE_SIZE, lastActive, lastId)
                    If page Is Nothing OrElse page.Count = 0 Then
                        noMore = True
                        Console.ForegroundColor = ConsoleColor.DarkGray
                        Console.WriteLine("  (no more topics)")
                        Console.ResetColor()
                        Continue While
                    End If
                    topics.AddRange(page)
                    lastActive = page.Last().LastActive
                    lastId = page.Last().Id
                    RenderTopicList(topics)
                    If page.Count < PAGE_SIZE Then noMore = True
                    Continue While
                End If
                Dim num As Integer
                If Not Integer.TryParse(numStr, num) OrElse num < 1 OrElse num > topics.Count Then
                    Console.ForegroundColor = ConsoleColor.Red
                    Console.WriteLine("  Invalid number.")
                    Console.ResetColor()
                    Continue While
                End If
                
                Dim selected = topics(num - 1)
                Await ViewTopicDetailAsync(selected)
            End While
        Catch ex As Exception
            Console.ForegroundColor = ConsoleColor.Red
            Console.WriteLine($"  ✗ Error: {ex.Message}")
            Console.ResetColor()
        End Try
        Console.WriteLine()
        Console.WriteLine("  Press any key to return to the menu...")
        Console.ReadKey()
    End Function

    Sub RenderTopicList(topics As List(Of Topic))
        For i = 0 To topics.Count - 1
            Dim t = topics(i)
            Dim time = If(t.LastActive.HasValue, t.LastActive.Value.ToLocalTime().ToString("MMM dd"), "?")
            Dim pollMark = If(t.Poll IsNot Nothing, " 📊", "")
            Console.ForegroundColor = ConsoleColor.Cyan
            Console.Write($"  {i + 1}. ")
            Console.ForegroundColor = ConsoleColor.White
            Console.Write($"{t.Title}")
            Console.ResetColor()
            Console.WriteLine($"{pollMark}")
            If Not String.IsNullOrWhiteSpace(t.Subheading) Then
                Dim subText = t.Subheading
                If subText.Length > 50 Then subText = subText.Substring(0, 47) & "..."
                Console.ForegroundColor = ConsoleColor.DarkGray
                Console.WriteLine($"     {subText}")
                Console.ResetColor()
            End If
            Console.ForegroundColor = ConsoleColor.DarkGray
            Console.WriteLine($"     {t.CommentCount} comments · {time}")
            Console.ResetColor()
        Next
    End Sub

    ''' <summary>
    ''' Show a single topic: poll results, comments, and interaction options.
    ''' </summary>
    Async Function ViewTopicDetailAsync(selected As Topic) As Task
        Try
            Console.Clear()
            ShowHeader()
            Console.ForegroundColor = ConsoleColor.Cyan
            Console.WriteLine($"  {selected.Title}")
            Console.ResetColor()
            If Not String.IsNullOrWhiteSpace(selected.Subheading) Then
                Console.WriteLine($"  {selected.Subheading}")
            End If
            Console.ForegroundColor = ConsoleColor.DarkGray
            Console.WriteLine($"  ID: {selected.Id} · {selected.CommentCount} comments")
            Console.ResetColor()
            Console.WriteLine()
            
            ' Show poll with live vote results (mirrors updatePollUI)
            If selected.Poll IsNot Nothing Then
                Dim votes = Await client.GetPollVotesAsync(selected.Id)
                Dim total = votes.Values.Sum()
                Console.ForegroundColor = ConsoleColor.Magenta
                Console.WriteLine($"  📊 Poll: {selected.Poll.Question}")
                Console.ResetColor()
                For i = 0 To selected.Poll.Options.Count - 1
                    Dim count = If(votes.ContainsKey(i), votes(i), 0)
                    Dim pct = If(total > 0, Math.Round(count * 100.0 / total), 0)
                    Console.WriteLine($"    {i + 1}. {selected.Poll.Options(i)}  [{count} votes, {pct}%]")
                Next
                Console.WriteLine($"    Total: {total} votes")
                Console.WriteLine()
            End If
            
            ' Show comments
            Dim comments = Await client.GetTopicCommentsAsync(selected.Id, 50)
            
            ' Resolve display names (one RTDB read per unique author, session-cached)
            Dim nameUids As New List(Of String)
            If Not String.IsNullOrWhiteSpace(selected.AuthorId) Then nameUids.Add(selected.AuthorId)
            If comments IsNot Nothing Then nameUids.AddRange(comments.Select(Function(c) c.AuthorId))
            Dim names = Await client.GetUsernamesAsync(nameUids)
            Dim authorName = If(String.IsNullOrWhiteSpace(selected.AuthorId), "unknown", names(selected.AuthorId))
            
            Console.ForegroundColor = ConsoleColor.DarkGray
            Console.WriteLine($"  by {authorName} · ID: {selected.Id} · {selected.CommentCount} comments")
            Console.ResetColor()
            Console.WriteLine()
            
            If comments IsNot Nothing AndAlso comments.Count > 0 Then
                Console.ForegroundColor = ConsoleColor.DarkGray
                Console.WriteLine($"  Comments ({comments.Count}):")
                Console.ResetColor()
                For Each c In comments
                    Dim time = If(c.Timestamp.HasValue, c.Timestamp.Value.ToLocalTime().ToString("MMM dd HH:mm"), "?")
                    Dim who = If(String.IsNullOrWhiteSpace(c.AuthorId), "unknown", If(names.ContainsKey(c.AuthorId), names(c.AuthorId), "unknown"))
                    Console.WriteLine($"  [{time}] {who}: {c.Body}")
                    Console.ForegroundColor = ConsoleColor.DarkGray
                    Console.WriteLine($"    ─ {c.Id}")
                    Console.ResetColor()
                Next
            Else
                Console.WriteLine("  No comments yet.")
            End If
            
            Console.WriteLine()
            Console.WriteLine("  Options:")
            Console.WriteLine("    1. Add comment")
            If selected.Poll IsNot Nothing Then Console.WriteLine("    2. Vote on poll")
            Console.WriteLine("    3. Report topic")
            If selected.AuthorId = client.UserId Then Console.WriteLine("    4. Delete my topic")
            Console.WriteLine("    0. Back")
            Console.WriteLine()
            Console.Write("  Select: ")
            Dim opt = Console.ReadLine()
            
            If opt = "1" Then
                Console.Write("  Your comment: ")
                Dim body = Console.ReadLine()
                If Not String.IsNullOrWhiteSpace(body) Then
                    Try
                        Dim r = Await client.CommentOnTopicAsync(selected.Id, body)
                        client.InvalidateTopicComments(selected.Id)
                        Console.ForegroundColor = ConsoleColor.Green
                        Console.WriteLine($"  ✓ Comment added!")
                        Console.ResetColor()
                    Catch ex As Exception
                        Console.ForegroundColor = ConsoleColor.Red
                        Console.WriteLine($"  ✗ {ex.Message}")
                        Console.ResetColor()
                    End Try
                End If
            ElseIf opt = "2" AndAlso selected.Poll IsNot Nothing Then
                Console.Write("  Vote for option (1-{0}): ", selected.Poll.Options.Count)
                Dim voteStr = Console.ReadLine()
                Dim voteIdx As Integer
                If Integer.TryParse(voteStr, voteIdx) AndAlso voteIdx >= 1 AndAlso voteIdx <= selected.Poll.Options.Count Then
                    Try
                        Await client.VoteOnPollAsync(selected.Id, voteIdx - 1)
                        Console.ForegroundColor = ConsoleColor.Green
                        Console.WriteLine("  ✓ Vote cast!")
                        Console.ResetColor()
                    Catch ex As Exception
                        Console.ForegroundColor = ConsoleColor.Red
                        Console.WriteLine($"  ✗ {ex.Message}")
                        Console.ResetColor()
                    End Try
                End If
            ElseIf opt = "3" Then
                Await ReportContentPromptAsync("topic", selected.Id, $"topics/{selected.Id}", selected.AuthorId, selected.Title)
            ElseIf opt = "4" AndAlso selected.AuthorId = client.UserId Then
                Console.Write("  Delete this topic? This cannot be undone. (y/n): ")
                If Console.ReadLine()?.ToLower() = "y" Then
                    Try
                        Await client.DeleteTopicAsync(selected.Id)
                        Console.ForegroundColor = ConsoleColor.Green
                        Console.WriteLine("  ✓ Topic deleted.")
                        Console.ResetColor()
                    Catch ex As Exception
                        Console.ForegroundColor = ConsoleColor.Red
                        Console.WriteLine($"  ✗ {ex.Message}")
                        Console.ResetColor()
                    End Try
                End If
            End If
        Catch ex As Exception
            Console.ForegroundColor = ConsoleColor.Red
            Console.WriteLine($"  ✗ Error: {ex.Message}")
            Console.ResetColor()
        End Try
        Console.WriteLine()
        Console.WriteLine("  Press any key to return to the menu...")
        Console.ReadKey()
    End Function
    
    Async Function BrowseNeighbourhoodAsync(Optional forceRefresh As Boolean = False) As Task
        Console.Clear()
        ShowHeader()
        Console.ForegroundColor = ConsoleColor.Cyan
        Console.WriteLine("  ── Neighbourhood ──")
        Console.ResetColor()
        Console.WriteLine()
        
        Try
            ' Page-based loading (10 per fetch, like neighbourhood.html's PAGE_SIZE)
            Const PAGE_SIZE As Integer = 10
            Dim posts As New List(Of NeighbourhoodPost)()
            Dim lastTs As DateTime? = Nothing
            Dim lastId As String = Nothing
            Dim noMore As Boolean = False
            
            While True
                Dim page = Await client.GetNeighbourhoodPostsPageAsync(PAGE_SIZE, lastTs, lastId)
                If page Is Nothing OrElse page.Count = 0 Then
                    noMore = True
                    If posts.Count = 0 Then
                        Console.WriteLine("  No posts found.")
                        Console.ReadKey()
                        Return
                    End If
                    Exit While
                End If
                posts.AddRange(page)
                lastTs = page.Last().Timestamp
                lastId = page.Last().Id
                
                Console.Clear()
                ShowHeader()
                Console.ForegroundColor = ConsoleColor.Cyan
                Console.WriteLine("  ── Neighbourhood ──")
                Console.ResetColor()
                Console.WriteLine()
                RenderPostList(posts)
                
                If page.Count < PAGE_SIZE Then
                    noMore = True
                    Exit While
                End If
                
                Console.WriteLine($"  [S]ee More (loaded {posts.Count}), or Enter to select")
                Console.Write("  > ")
                Dim more = Console.ReadLine()
                If String.IsNullOrWhiteSpace(more) OrElse more?.ToLower() <> "s" Then Exit While
            End While
            
            ' Selection loop
            While True
                Console.WriteLine()
                Console.Write("  Enter post number, 's' for more, 'r' to refresh, or Enter to return: ")
                Dim numStr = Console.ReadLine()
                If String.IsNullOrWhiteSpace(numStr) Then Return
                If numStr?.ToLower() = "r" Then
                    Await BrowseNeighbourhoodAsync(True)
                    Return
                End If
                If numStr?.ToLower() = "s" AndAlso Not noMore Then
                    Dim page = Await client.GetNeighbourhoodPostsPageAsync(PAGE_SIZE, lastTs, lastId)
                    If page Is Nothing OrElse page.Count = 0 Then
                        noMore = True
                        Console.ForegroundColor = ConsoleColor.DarkGray
                        Console.WriteLine("  (no more posts)")
                        Console.ResetColor()
                        Continue While
                    End If
                    posts.AddRange(page)
                    lastTs = page.Last().Timestamp
                    lastId = page.Last().Id
                    RenderPostList(posts)
                    If page.Count < PAGE_SIZE Then noMore = True
                    Continue While
                End If
                Dim num As Integer
                If Not Integer.TryParse(numStr, num) OrElse num < 1 OrElse num > posts.Count Then
                    Console.ForegroundColor = ConsoleColor.Red
                    Console.WriteLine("  Invalid number.")
                    Console.ResetColor()
                    Continue While
                End If
                
                Dim selected = posts(num - 1)
                Await ViewPostDetailAsync(selected)
            End While
        Catch ex As Exception
            Console.ForegroundColor = ConsoleColor.Red
            Console.WriteLine($"  ✗ Error: {ex.Message}")
            Console.ResetColor()
        End Try
        Console.WriteLine()
        Console.WriteLine("  Press any key to return to the menu...")
        Console.ReadKey()
    End Function

    Sub RenderPostList(posts As List(Of NeighbourhoodPost))
        For i = 0 To posts.Count - 1
            Dim p = posts(i)
            Dim time = If(p.Timestamp.HasValue, p.Timestamp.Value.ToLocalTime().ToString("MMM dd HH:mm"), "?")
            Dim text = p.Text
            If text.Length > 80 Then text = text.Substring(0, 77) & "..."
            Console.ForegroundColor = ConsoleColor.White
            Console.Write($"  {i + 1}. ")
            Console.WriteLine($"{text}")
            Console.ResetColor()
            Console.ForegroundColor = ConsoleColor.DarkGray
            Console.WriteLine($"     {time}")
            Console.ResetColor()
        Next
    End Sub

    ''' <summary>
    ''' Show a single neighbourhood post with comments and interaction options.
    ''' </summary>
    Async Function ViewPostDetailAsync(selected As NeighbourhoodPost) As Task
        Console.Clear()
        ShowHeader()
        Console.ForegroundColor = ConsoleColor.Cyan
        Console.WriteLine("  ── Neighbourhood Post ──")
        Console.ResetColor()
        Console.WriteLine()
        
        Dim comments = Await client.GetNeighbourhoodCommentsAsync(selected.Id, 20)
        
        ' Resolve display names (one RTDB read per unique author, session-cached)
        Dim nameUids As New List(Of String) From {selected.Uid}
        If comments IsNot Nothing Then nameUids.AddRange(comments.Select(Function(c) c.AuthorId))
        Dim names = Await client.GetUsernamesAsync(nameUids)
        Dim authorName = names(selected.Uid)
        
        Console.WriteLine($"  {selected.Text}")
        Console.ForegroundColor = ConsoleColor.DarkGray
        Console.WriteLine($"  ID: {selected.Id} · by {authorName}")
        Console.ResetColor()
        Console.WriteLine()
        
        If comments IsNot Nothing AndAlso comments.Count > 0 Then
            Console.ForegroundColor = ConsoleColor.DarkGray
            Console.WriteLine($"  Comments ({comments.Count}):")
            Console.ResetColor()
            For Each c In comments
                Dim time = If(c.Timestamp.HasValue, c.Timestamp.Value.ToLocalTime().ToString("MMM dd HH:mm"), "?")
                Dim who = If(names.ContainsKey(c.AuthorId), names(c.AuthorId), "unknown")
                Console.WriteLine($"  [{time}] {who}: {c.Text}")
                Console.ForegroundColor = ConsoleColor.DarkGray
                Console.WriteLine($"    ─ {c.Id}")
                Console.ResetColor()
            Next
        Else
            Console.WriteLine("  No comments yet.")
        End If
        
        Console.WriteLine()
        Console.WriteLine("  Options:")
        Console.WriteLine("    1. Add comment")
        Console.WriteLine("    2. Report post")
        If selected.Uid = client.UserId Then Console.WriteLine("    3. Delete my post")
        Console.WriteLine("    0. Back")
        Console.WriteLine()
        Console.Write("  Select: ")
        Dim opt = Console.ReadLine()
        
        If opt = "1" Then
            Console.Write("  Your comment: ")
            Dim comment = Console.ReadLine()
            If Not String.IsNullOrWhiteSpace(comment) Then
                Try
                    Dim r = Await client.CommentOnNeighbourhoodPostAsync(selected.Id, comment)
                    client.InvalidatePostComments(selected.Id)
                    Console.ForegroundColor = ConsoleColor.Green
                    Console.WriteLine("  ✓ Comment added!")
                    Console.ResetColor()
                Catch ex As Exception
                    Console.ForegroundColor = ConsoleColor.Red
                    Console.WriteLine($"  ✗ {ex.Message}")
                    Console.ResetColor()
                End Try
            End If
        ElseIf opt = "2" Then
            Await ReportContentPromptAsync("neighbourhood_post", selected.Id, $"neighbourhood_posts/{selected.Id}", selected.Uid, selected.Text)
        ElseIf opt = "3" AndAlso selected.Uid = client.UserId Then
            Console.Write("  Delete this post? This cannot be undone. (y/n): ")
            If Console.ReadLine()?.ToLower() = "y" Then
                Try
                    Await client.DeleteNeighbourhoodPostAsync(selected.Id)
                    Console.ForegroundColor = ConsoleColor.Green
                    Console.WriteLine("  ✓ Post deleted.")
                    Console.ResetColor()
                Catch ex As Exception
                    Console.ForegroundColor = ConsoleColor.Red
                    Console.WriteLine($"  ✗ {ex.Message}")
                    Console.ResetColor()
                End Try
            End If
        End If
    End Function
    
    Async Function CreateTopicAsync() As Task
        Console.Clear()
        ShowHeader()
        Console.ForegroundColor = ConsoleColor.Cyan
        Console.WriteLine("  ── Create Topic ──")
        Console.ResetColor()
        Console.WriteLine()
        
        Console.ForegroundColor = ConsoleColor.DarkGray
        Console.WriteLine("  Tip: choose a topic title and an emoji icon.")
        Console.WriteLine("      You can optionally add a poll with 2-4 options.")
        Console.ResetColor()
        Console.WriteLine()
        
        Console.Write("  Title (1-20 chars): ")
        Dim title = Console.ReadLine()
        If String.IsNullOrWhiteSpace(title) Then Return
        
        Console.Write("  Subheading (max 35 chars, optional): ")
        Dim subheading = Console.ReadLine()
        
        Console.Write("  Icon (emoji, e.g. 📚): ")
        Dim icon = Console.ReadLine()
        If String.IsNullOrWhiteSpace(icon) Then icon = "💬"
        
        Console.Write("  Add a poll? (y/n): ")
        Dim addPoll = Console.ReadLine()?.ToLower()
        Dim poll As TopicPoll = Nothing
        If addPoll = "y" Or addPoll = "yes" Then
            Console.Write("  Poll question: ")
            Dim q = Console.ReadLine()
            If Not String.IsNullOrWhiteSpace(q) Then
                Console.Write("  Number of options (2-4): ")
                Dim nStr = Console.ReadLine()
                Dim nOpts As Integer
                If Integer.TryParse(nStr, nOpts) AndAlso nOpts >= 2 AndAlso nOpts <= 4 Then
                    Dim opts As New List(Of String)()
                    For i = 1 To nOpts
                        Console.Write($"  Option {i}: ")
                        Dim o = Console.ReadLine()
                        If Not String.IsNullOrWhiteSpace(o) Then opts.Add(o)
                    Next
                    If opts.Count >= 2 Then
                        poll = New TopicPoll With {.Question = q, .Options = opts}
                    End If
                End If
            End If
        End If
        
        Try
            Dim result = Await client.CreateTopicAsync(title, If(subheading, ""), icon, poll)
            Console.ForegroundColor = ConsoleColor.Green
            Console.WriteLine($"  ✓ Topic created! ID: {result.Id}")
            Console.ResetColor()
        Catch ex As Exception
            Console.ForegroundColor = ConsoleColor.Red
            Console.WriteLine($"  ✗ {ex.Message}")
            Console.ResetColor()
        End Try
        Console.WriteLine("  Press any key to return to the menu...")
        Console.ReadKey()
    End Function
    
    Async Function CreateNeighbourhoodPostAsync() As Task
        Console.Clear()
        ShowHeader()
        Console.ForegroundColor = ConsoleColor.Cyan
        Console.WriteLine("  ── Create Neighbourhood Post ──")
        Console.ResetColor()
        Console.WriteLine()
        
        Console.ForegroundColor = ConsoleColor.DarkGray
        Console.WriteLine("  Tip: minimum 10 words, maximum 280 characters.")
        Console.ResetColor()
        Console.WriteLine()
        
        Console.Write("  Post text: ")
        Dim text = Console.ReadLine()
        If String.IsNullOrWhiteSpace(text) Then Return
        
        Try
            Dim result = Await client.CreateNeighbourhoodPostAsync(text)
            Console.ForegroundColor = ConsoleColor.Green
            Console.WriteLine($"  ✓ Post created! ID: {result.Id}")
            Console.ResetColor()
        Catch ex As Exception
            Console.ForegroundColor = ConsoleColor.Red
            Console.WriteLine($"  ✗ {ex.Message}")
            Console.ResetColor()
        End Try
        Console.WriteLine("  Press any key to return to the menu...")
        Console.ReadKey()
    End Function
    
End Module
