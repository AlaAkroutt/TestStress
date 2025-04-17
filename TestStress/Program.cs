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
        public static  int USERS = int.Parse( Environment.GetEnvironmentVariable("users")); // Number of concurrent simulated users
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

               
                 

                // Step 3: Connect to SignalR
                var connection = new HubConnectionBuilder()
                    .WithUrl($"{BASE_URL}/api/notificationsHub")
                    .WithAutomaticReconnect()
                    .Build();

                bool cardSelected = false;
                Card selectedCard = null;

       
                // Handle "NumberSelected" event (user selects a number)
              
                // Handle "Timer" event (game countdown)
             
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