using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using Microsoft.AspNetCore.SignalR.Client;
using Newtonsoft.Json;
using Microsoft.AspNetCore.SignalR;

namespace BingoSignalRClient
{
    class Program
    {
        private const string BASE_URL = "https://bingo-backend.zetabox.tn";
        private const int USERS = 1000; // Number of concurrent simulated users
        private static int fail = 0;
        private static int notif = 0;
        private static int duplicateUsers = 0;
        private static int apiErrors = 0;
        private static int signalRErrors = 0;
        private static readonly object lockObject = new object();

        // Track user IDs to ensure uniqueness
        private static readonly HashSet<int> usedUserIds = new HashSet<int>();
        private static readonly object userIdsLock = new object();

        // Console logging only
        private static readonly object consoleLock = new object();

        // Log levels
        private enum LogLevel { Debug, Info, Warning, Error, Critical }

        static async Task Main(string[] args)
        {
            // Start simulation with console message

            LogMessage(LogLevel.Info, "Starting Bingo SignalR Client Simulation");

            // Create a semaphore to limit concurrent connections if needed
            var semaphore = new SemaphoreSlim(1000); // Limit to 1000 concurrent operations
            var tasks = new List<Task>();

            for (int i = 0; i < USERS; i++)
            {
                int userIndex = i;
                await semaphore.WaitAsync(); // Wait for a slot to be available

                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        await SimulateUser(userIndex);
                    }
                    finally
                    {
                        semaphore.Release(); // Release the slot when done
                    }
                }));

                // Add slight delay between user spawns
                await Task.Delay(100);

                lock (lockObject)
                {
                    if (i % 100 == 0 || i == USERS - 1) // Log every 100 users and at the end
                    {
                        LogMessage(LogLevel.Info, $"Progress: {i + 1}/{USERS} users started");
                        LogMessage(LogLevel.Info, $"Current stats - Failures: {fail}, Notifications: {notif}");
                        LogMessage(LogLevel.Info, $"Error breakdown - API: {apiErrors}, SignalR: {signalRErrors}, Duplicates: {duplicateUsers}");
                    }
                }
            }

            // Wait for all tasks to complete
            await Task.WhenAll(tasks);

            // Log final statistics
            LogMessage(LogLevel.Info, "Simulation completed");
            LogMessage(LogLevel.Info, $"Final statistics: Users: {USERS}, Failures: {fail}, Notifications: {notif}");
            LogMessage(LogLevel.Info, $"Error breakdown: API Errors: {apiErrors}, SignalR Errors: {signalRErrors}, Duplicate Users: {duplicateUsers}");
        }

        private static async Task SimulateUser(int userIndex)
        {
            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            try
            {
                LogMessage(LogLevel.Info, $"User {userIndex}: Starting...");

                // Step 1: Get codeClient with uniqueness check
                UserData userData = null;
                bool isUniqueUser = false;
                int maxRetries = 5;
                int retryCount = 0;

                while (!isUniqueUser && retryCount < maxRetries)
                {
                    var content = new StringContent("", Encoding.UTF8, "application/json");
                    var response = await httpClient.PostAsync($"{BASE_URL}/api/Utilisateur?tokenUser=0", content);
                    response.EnsureSuccessStatusCode();

                    var responseBody = await response.Content.ReadAsStringAsync();
                    userData = JsonConvert.DeserializeObject<UserData>(responseBody);

                    // Check if this user ID is unique
                    lock (userIdsLock)
                    {
                        if (!usedUserIds.Contains(userData.Id))
                        {
                            // Add to our tracking set
                            usedUserIds.Add(userData.Id);
                            isUniqueUser = true;
                            LogMessage(LogLevel.Info, $"User {userIndex}: Got unique user ID {userData.Id}");
                        }
                        else
                        {
                            retryCount++;
                            LogMessage(LogLevel.Warning, $"User {userIndex}: Got duplicate user ID {userData.Id}, retrying ({retryCount}/{maxRetries})...");
                            Interlocked.Increment(ref duplicateUsers);
                            // Add a small delay before retrying
                            Task.Delay(500).Wait();
                        }
                    }
                }

                // If we couldn't get a unique user after max retries, throw an exception
                if (!isUniqueUser)
                {
                    var errorMsg = $"Failed to get a unique user ID after {maxRetries} retries";
                    LogMessage(LogLevel.Error, $"User {userIndex}: {errorMsg}");
                    throw new Exception(errorMsg);
                }

                // Step 2: Login
                var loginData = new { id = userData.Id, codeClient = userData.CodeClient };
                var loginContent = new StringContent(JsonConvert.SerializeObject(loginData), Encoding.UTF8, "application/json");
                var loginResponse = await httpClient.PostAsync($"{BASE_URL}/api/Utilisateur/login", loginContent);
                loginResponse.EnsureSuccessStatusCode();

                var loginResponseBody = await loginResponse.Content.ReadAsStringAsync();
                var tokenData = JsonConvert.DeserializeObject<TokenData>(loginResponseBody);
                string token = tokenData.AccessToken;

                // Update headers with token
                httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

                // Step 3: Connect to SignalR
                var connection = new HubConnectionBuilder()
                    .WithUrl($"{BASE_URL}/api/notificationsHub", options =>
                    {
                        options.AccessTokenProvider = () => Task.FromResult(token);
                    })
                    .WithAutomaticReconnect()
                    .Build();

                bool cardSelected = false;
                Card selectedCard = null;

                // Handle "status" event (game progress)
                connection.On<string>("status", async (status) =>
                {
                LogMessage(LogLevel.Debug, $"User {userIndex}: SignalR status = {status}");

                    if (status == "distribution_in_progress" && !cardSelected)
                    {
                        // Step 4: Get Cards
                        try
                        {
                            var cardsResponse = await httpClient.GetAsync($"{BASE_URL}/api/Card");
                            cardsResponse.EnsureSuccessStatusCode();

                            var cardsResponseBody = await cardsResponse.Content.ReadAsStringAsync();
                            var cards = JsonConvert.DeserializeObject<List<Card>>(cardsResponseBody);

                            if (cards == null)
                            {
                                LogMessage(LogLevel.Error, $"User {userIndex}: Failed to deserialize cards response");
                                return;
                            }

                            if (cards.Count > 0)
                            {
                                try
                                {
                                    var selectedId = cards[0].Id; // Select the first card for simplicity
                                    var random = new Random();
                                    var randomDelay = random.Next(60000); // Random delay up to 60 seconds

                                    LogMessage(LogLevel.Debug, $"User {userIndex}: Waiting {randomDelay}ms before selecting card");
                                    // Use Task.Delay instead of setTimeout
                                    await Task.Delay(randomDelay);

                                    try
                                    {
                                        var selectCardData = new { id = selectedId };
                                        var selectCardContent = new StringContent(
                                            JsonConvert.SerializeObject(selectCardData),
                                            Encoding.UTF8,
                                            "application/json"
                                        );

                                        var selectCardResponse = await httpClient.PostAsync($"{BASE_URL}/api/Card/Select", selectCardContent);
                                        selectCardResponse.EnsureSuccessStatusCode();

                                        LogMessage(LogLevel.Info, $"User {userIndex}: Card selected after {randomDelay}ms");
                                        Interlocked.Increment(ref notif);
                                        cardSelected = true;
                                        LogMessage(LogLevel.Info, $"User {userIndex}: Card selection successful");
                                    }
                                    catch (HttpRequestException ex)
                                    {
                                        LogMessage(LogLevel.Error, $"User {userIndex}: Failed to select card (HTTP): {ex.Message}");
                                        Interlocked.Increment(ref apiErrors);
                                    }
                                    catch (System.Text.Json.JsonException ex)
                                    {
                                        LogMessage(LogLevel.Error, $"User {userIndex}: Failed to serialize card selection data: {ex.Message}");
                                    }
                                    catch (TaskCanceledException ex)
                                    {
                                        LogMessage(LogLevel.Warning, $"User {userIndex}: Card selection request was cancelled: {ex.Message}");
                                    }
                                    catch (Exception ex)
                                    {
                                        LogMessage(LogLevel.Error, $"User {userIndex}: Failed to select card (general error): {ex.Message}");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    LogMessage(LogLevel.Error, $"User {userIndex}: Error preparing card selection: {ex.Message}");
                                }
                            }
                        }
                        catch (HttpRequestException ex)
                        {
                            LogMessage(LogLevel.Error, $"User {userIndex}: Failed to get cards (HTTP): {ex.Message}");
                            Interlocked.Increment(ref apiErrors);
                        }
                        catch (System.Text.Json.JsonException ex)
                        {
                            LogMessage(LogLevel.Error, $"User {userIndex}: Failed to parse cards response: {ex.Message}");
                        }
                        catch (Exception ex)
                        {
                            LogMessage(LogLevel.Error, $"User {userIndex}: Failed to get cards (general error): {ex.Message}");
                        }


                        if (status == "emission_in_progress")
                        {
                            try
                            {
                                // Step 5: Get the selected card
                                var selectedCardResponse = await httpClient.GetAsync($"{BASE_URL}/api/Card/GetSelectedCard");
                                selectedCardResponse.EnsureSuccessStatusCode();

                                var selectedCardResponseBody = await selectedCardResponse.Content.ReadAsStringAsync();
                                var selectedCards = JsonConvert.DeserializeObject<List<Card>>(selectedCardResponseBody);

                                if (selectedCards == null)
                                {
                                    LogMessage(LogLevel.Error, $"User {userIndex}: Failed to deserialize selected card response");
                                    return;
                                }

                                if (selectedCards.Count > 0)
                                {
                                    selectedCard = selectedCards[0];
                                    LogMessage(LogLevel.Info, $"User {userIndex}: Selected card loaded");
                                }
                                else
                                {
                                    LogMessage(LogLevel.Warning, $"User {userIndex}: No selected card available");
                                }
                            }
                            catch (HttpRequestException ex)
                            {
                                LogMessage(LogLevel.Error, $"User {userIndex}: Failed to get selected card (HTTP): {ex.Message}");
                                Interlocked.Increment(ref apiErrors);
                            }
                            catch (System.Text.Json.JsonException ex)
                            {
                                LogMessage(LogLevel.Error, $"User {userIndex}: Failed to parse selected card response: {ex.Message}");
                            }
                            catch (Exception ex)
                            {
                                LogMessage(LogLevel.Error, $"User {userIndex}: Failed to get selected card (general error): {ex.Message}");
                            }
                        }
                    }
                });

                // Handle "NumberSelected" event (user selects a number)
                connection.On<int>("NumberSelected", async (number) =>
                {
                    try
                    {
                        if (selectedCard == null)
                        {
                            LogMessage(LogLevel.Debug, $"User {userIndex}: No selected card available for number {number}");
                            return;
                        }

                        if (selectedCard.Cards == null)
                        {
                            LogMessage(LogLevel.Error, $"User {userIndex}: Selected card has null Cards property");
                            return;
                        }

                        try
                        {
                            // Flatten the 2D array of numbers
                            var flatNumbers = selectedCard.Cards.SelectMany(row => row).ToList();

                            if (flatNumbers.Contains(number))
                            {
                                int score = 10; // or based on some logic
                                var random = new Random();
                                var randomDelay = random.Next(5000); // Random delay up to 5 seconds

                                LogMessage(LogLevel.Debug, $"User {userIndex}: Waiting {randomDelay}ms before sending number {number}");
                                await Task.Delay(randomDelay);

                                try
                                {
                                    var numberData = new[] { number, score };
                                    var numberContent = new StringContent(
                                        JsonConvert.SerializeObject(numberData),
                                        Encoding.UTF8,
                                        "application/json"
                                    );

                                    var numberResponse = await httpClient.PostAsync($"{BASE_URL}/api/SelectedNumberClient/Number", numberContent);
                                    numberResponse.EnsureSuccessStatusCode();

                                    LogMessage(LogLevel.Info, $"User {userIndex}: Sent selected number {number} after {randomDelay}ms");
                                }
                                catch (HttpRequestException ex)
                                {
                                    LogMessage(LogLevel.Error, $"User {userIndex}: HTTP error sending selected number: {ex.Message}");
                                    Interlocked.Increment(ref apiErrors);
                                }
                                catch (System.Text.Json.JsonException ex)
                                {
                                    LogMessage(LogLevel.Error, $"User {userIndex}: JSON error sending selected number: {ex.Message}");
                                }
                                catch (Exception ex)
                                {
                                    LogMessage(LogLevel.Error, $"User {userIndex}: General error sending selected number: {ex.Message}");
                                }
                            }
                            else
                            {
                                LogMessage(LogLevel.Debug, $"User {userIndex}: Number {number} not in card");
                            }
                        }
                        catch (Exception ex)
                        {
                            LogMessage(LogLevel.Error, $"User {userIndex}: Error processing number {number}: {ex.Message}");
                        }
                    }
                    catch (Exception ex)
                    {
                        LogMessage(LogLevel.Error, $"User {userIndex}: Unhandled error in NumberSelected handler: {ex.Message}");
                    }
                });

                // Handle "Timer" event (game countdown)
                connection.On<int>("Timer", (timeLeft) =>
                {
                    try
                    {
                        LogMessage(LogLevel.Debug, $"User {userIndex}: Timer = {timeLeft}");
                    }
                    catch (Exception ex)
                    {
                        LogMessage(LogLevel.Error, $"User {userIndex}: Error in Timer handler: {ex.Message}");
                    }
                });

                // Step 6: Start SignalR connection
                try
                {
                    await connection.StartAsync();
                    LogMessage(LogLevel.Info, $"User {userIndex}: SignalR connection started");
                }
                catch (Exception ex)
                {
                    LogMessage(LogLevel.Error, $"User {userIndex}: Failed to start SignalR connection: {ex.Message}");
                    Interlocked.Increment(ref signalRErrors);
                    throw; // Rethrow to be caught by the outer catch block
                }

                // Keep the connection alive for the simulation
                await Task.Delay(TimeSpan.FromHours(1));
            }
            catch (HttpRequestException ex)
            {
                LogMessage(LogLevel.Error, $"User {userIndex} API error: {ex.Message}");
                Interlocked.Increment(ref fail);
                Interlocked.Increment(ref apiErrors);
            }
            catch (HubException ex)
            {
                LogMessage(LogLevel.Error, $"User {userIndex} SignalR error: {ex.Message}");
                Interlocked.Increment(ref fail);
                Interlocked.Increment(ref signalRErrors);
            }
            catch (Exception ex)
            {
                LogMessage(LogLevel.Error, $"User {userIndex} error: {ex.Message}");
                Interlocked.Increment(ref fail);
            }
        }
        // Helper method for logging to console only
        private static void LogMessage(LogLevel level, string message)
        {
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var logMessage = $"[{timestamp}] [{level}] {message}";

            // Write to console - thread-safe with lock
            lock (consoleLock)
            {
                Console.WriteLine(logMessage);
            }
        }
    }

    // Data models
    public class UserData
    {
        [JsonProperty("id")]
        public int Id { get; set; }

        [JsonProperty("codeClient")]
        public string CodeClient { get; set; }
    }

    public class TokenData
    {
        [JsonProperty("accessToken")]
        public string AccessToken { get; set; }
    }

    public class Card
    {
        [JsonProperty("id")]
        public int Id { get; set; }

        [JsonProperty("cards")]
        public List<List<int>> Cards { get; set; }
    }
}