using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Client;
using Newtonsoft.Json;

namespace BingoSignalRClient
{
    class Program
    {
        private const string BASE_URL = "https://bingo-backend.zetabox.tn";
        private static int USERS = int.Parse(Environment.GetEnvironmentVariable("users")); // Number of concurrent simulated users
        private static int fail = 0;
        private static int notif = 0;
        private static readonly object lockObject = new object();

        static async Task Main(string[] args)
        {
            Console.WriteLine("Starting Bingo SignalR Client Simulation");

            // Create a semaphore to limit concurrent connections if needed
            var semaphore = new SemaphoreSlim(USERS); // Limit to 100 concurrent operations
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
                    Console.WriteLine($"Fail: {fail}");
                    Console.WriteLine($"Notifications: {notif}");
                }
            }

            // Wait for all tasks to complete
            await Task.WhenAll(tasks);
            Console.WriteLine("Simulation completed");
        }

        private static async Task SimulateUser(int userIndex)
        {
            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            try
            {
                Console.WriteLine($"User {userIndex}: Starting...");

                // Step 1: Get codeClient
                var content = new StringContent("", Encoding.UTF8, "application/json");
                var response = await httpClient.PostAsync($"{BASE_URL}/api/Utilisateur?tokenUser=0", content);
                response.EnsureSuccessStatusCode();

                var responseBody = await response.Content.ReadAsStringAsync();
                var userData = JsonConvert.DeserializeObject<UserData>(responseBody);

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
                    Console.WriteLine($"User {userIndex}: SignalR status = {status}");

                    if (status == "distribution_in_progress" && !cardSelected)
                    {
                        // Step 4: Get Cards
                        var cardsResponse = await httpClient.GetAsync($"{BASE_URL}/api/Card");
                        cardsResponse.EnsureSuccessStatusCode();

                        var cardsResponseBody = await cardsResponse.Content.ReadAsStringAsync();
                        var cards = JsonConvert.DeserializeObject<List<Card>>(cardsResponseBody);

                        if (cards.Count > 0)
                        {
                            var selectedId = cards[0].Id; // Select the first card for simplicity
                            var random = new Random();
                            var randomDelay = random.Next(60000); // Random delay up to 60 seconds

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

                                Console.WriteLine($"User {userIndex}: Card selected after {randomDelay}ms");
                                Interlocked.Increment(ref notif);
                                cardSelected = true;
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"User {userIndex}: Failed to select card: {ex.Message}");
                            }
                        }
                    }

                    if (status == "emission_in_progress")
                    {
                        // Step 5: Get the selected card
                        var selectedCardResponse = await httpClient.GetAsync($"{BASE_URL}/api/Card/GetSelectedCard");
                        selectedCardResponse.EnsureSuccessStatusCode();

                        var selectedCardResponseBody = await selectedCardResponse.Content.ReadAsStringAsync();
                        var selectedCards = JsonConvert.DeserializeObject<List<Card>>(selectedCardResponseBody);

                        if (selectedCards.Count > 0)
                        {
                            selectedCard = selectedCards[0];
                            Console.WriteLine($"User {userIndex}: Selected card loaded");
                        }
                    }
                });

                // Handle "NumberSelected" event (user selects a number)
                connection.On<int>("NumberSelected", async (number) =>
                {
                    if (selectedCard == null) return;

                    // Flatten the 2D array of numbers
                    var flatNumbers = selectedCard.Cards.SelectMany(row => row).ToList();

                    if (flatNumbers.Contains(number))
                    {
                        int score = 10; // or based on some logic
                        var random = new Random();
                        var randomDelay = random.Next(5000); // Random delay up to 5 seconds

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

                            Console.WriteLine($"User {userIndex}: Sent selected number {number} after {randomDelay}ms");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"User {userIndex}: Error sending selected number: {ex.Message}");
                        }
                    }
                    else
                    {
                        Console.WriteLine($"User {userIndex}: Number {number} not in card");
                    }
                });

                // Handle "Timer" event (game countdown)
                connection.On<int>("Timer", (timeLeft) =>
                {
                    Console.WriteLine($"User {userIndex}: Timer = {timeLeft}");
                });

                // Step 6: Start SignalR connection
                await connection.StartAsync();
                Console.WriteLine($"User {userIndex}: SignalR connection started");

                // Keep the connection alive for the simulation
                await Task.Delay(TimeSpan.FromHours(1));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"User {userIndex} error: {ex.Message}");
                Interlocked.Increment(ref fail);
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