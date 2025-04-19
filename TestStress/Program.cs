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
        private const int USERS = 1000; // Number of concurrent simulated users
        private static int fail = 0;
        private static int notif = 0;
        private static readonly object lockObject = new object();

        // Track card IDs to detect duplicates across users
        private static readonly Dictionary<int, int> cardIdToUserMap = new Dictionary<int, int>();
        private static readonly object cardMapLock = new object();

        // Track selected numbers for each user to prevent duplicate selections
        private static readonly Dictionary<int, HashSet<int>> userSelectedNumbers = new Dictionary<int, HashSet<int>>();
        private static readonly object selectedNumbersLock = new object();

        static async Task Main(string[] args)
        {
            Console.WriteLine("Starting Bingo SignalR Client Simulation");

            // Create a list to hold all user simulation tasks
            var tasks = new List<Task>();

            for (int i = 0; i < USERS; i++)
            {
                int userIndex = i;

                // Run each user simulation without semaphore limiting
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        await SimulateUser(userIndex);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error in user {userIndex} simulation: {ex.Message}");
                    }
                }));

                // Add slight delay between user spawns
                await Task.Delay(50);

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
                Console.WriteLine($"User {userIndex}: Getting codeClient...");
                var content = new StringContent("", Encoding.UTF8, "application/json");
                var response = await httpClient.PostAsync($"{BASE_URL}/api/Utilisateur?tokenUser=0", content);
                response.EnsureSuccessStatusCode();

                var responseBody = await response.Content.ReadAsStringAsync();
                var userData = JsonConvert.DeserializeObject<UserData>(responseBody);

                Console.WriteLine($"User {userIndex}: Got user ID {userData.Id}");

                // Step 2: Login
                Console.WriteLine($"User {userIndex}: Logging in...");
                var loginData = new { id = userData.Id, codeClient = userData.CodeClient };
                var loginContent = new StringContent(JsonConvert.SerializeObject(loginData), Encoding.UTF8, "application/json");
                var loginResponse = await httpClient.PostAsync($"{BASE_URL}/api/Utilisateur/login", loginContent);
                loginResponse.EnsureSuccessStatusCode();

                var loginResponseBody = await loginResponse.Content.ReadAsStringAsync();
                Console.WriteLine($"User {userIndex}: Logged in");
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
                List<Card> cards = null;

                // Handle "status" event (game progress)
                connection.On<string>("status", async (status) =>
                {
                    Console.WriteLine($"User {userIndex}: SignalR status = {status}");

                    if (status == "distribution_in_progress" && !cardSelected)
                    {
                        // Step 4: Get Cards
                        Console.WriteLine($"User {userIndex}: Getting cards...");
                        bool foundUniqueCards = false;
                        int maxRetries = 10;
                        int retryCount = 0;

                        while (!foundUniqueCards && retryCount < maxRetries)
                        {
                            try
                            {
                                Console.WriteLine($"User {userIndex}: Attempt {retryCount + 1} to get unique cards...");
                                var cardsResponse = await httpClient.GetAsync($"{BASE_URL}/api/Card");
                                cardsResponse.EnsureSuccessStatusCode();

                                var cardsResponseBody = await cardsResponse.Content.ReadAsStringAsync();
                                cards = JsonConvert.DeserializeObject<List<Card>>(cardsResponseBody);

                                if (cards == null || cards.Count == 0)
                                {
                                    throw new Exception("Received empty or null card list");
                                }

                                Console.WriteLine($"User {userIndex}: Got cards ids {string.Join(", ", cards.Select(c => c.Id))}");
                                // Check if these cards have been assigned to other users
                                bool hasDuplicateCards = false;
                                string duplicateDetails = "";
                                Dictionary<int, int> tempCardAssignments = new Dictionary<int, int>();

                                lock (cardMapLock)
                                {
                                    foreach (var card in cards ?? new List<Card>())
                                    {
                                        if (cardIdToUserMap.TryGetValue(card.Id, out int existingUserId))
                                        {
                                            hasDuplicateCards = true;
                                            duplicateDetails += $"Card {card.Id} already assigned to user {existingUserId}. ";
                                        }
                                        else
                                        {
                                            // Track this card as a potential assignment
                                            tempCardAssignments[card.Id] = userData.Id;
                                        }
                                    }

                                    if (!hasDuplicateCards)
                                    {
                                        // No duplicates found, record these cards as assigned to this user
                                        foreach (var entry in tempCardAssignments)
                                        {
                                            cardIdToUserMap[entry.Key] = entry.Value;
                                        }
                                        foundUniqueCards = true;
                                    }
                                    else
                                    {
                                        retryCount++;
                                        Console.WriteLine($"WARNING: User {userIndex} with user ID {userData.Id}: Received duplicate cards! {duplicateDetails} Retrying ({retryCount}/{maxRetries})...");
                                        hasDuplicateCards = true; // Set flag to use outside lock
                                    }
                                }

                                // Wait a bit before retrying if duplicates were found
                                if (hasDuplicateCards)
                                {
                                    await Task.Delay(10);
                                }
                            }
                            catch (Exception ex)
                            {
                                retryCount++;
                                Console.WriteLine($"User {userIndex}: Error getting cards: {ex.Message}. Retrying ({retryCount}/{maxRetries})...");
                                await Task.Delay(500); // Wait a bit longer after an error
                            }
                        }

                        if (!foundUniqueCards)
                        {
                            Console.WriteLine($"ERROR: User {userIndex} with user ID {userData.Id}: Could not get unique cards after {maxRetries} retries. Proceeding with potentially duplicate cards.");

                            // As a last resort, record these cards as assigned to this user
                            lock (cardMapLock)
                            {
                                foreach (var card in cards ?? new List<Card>())
                                {
                                    cardIdToUserMap[card.Id] = userData.Id;
                                }
                            }
                        }

                        Console.WriteLine($"User {userIndex} with user ID {userData.Id}: Got {cards?.Count ?? 0} cards with ids: {string.Join(", ", cards?.Select(c => c.Id) ?? new List<int>())}");

                        if (cards?.Count > 0)
                        {
                            var selectedId = cards[0].Id; // Select the first card for simplicity
                            var random = new Random();
                            var randomDelay = random.Next(100); // Random delay up to 60 seconds

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
                        var random = new Random();
                        var randomDelay = random.Next(100); // Random delay up to 60 seconds

                        // Use Task.Delay instead of setTimeout
                        await Task.Delay(randomDelay);

                        // try
                        // {
                        //     // Step 5: Get the selected card
                        //     Console.WriteLine($"User {userIndex}: Getting selected card...");
                        //     var selectedCardResponse = await httpClient.GetAsync($"{BASE_URL}/api/Card/GetSelectedCard");
                        //     selectedCardResponse.EnsureSuccessStatusCode();

                        //     var selectedCardResponseBody = await selectedCardResponse.Content.ReadAsStringAsync();
                        //     var selectedCards = JsonConvert.DeserializeObject<List<Card>>(selectedCardResponseBody);

                        //     if (selectedCards == null || selectedCards.Count == 0)
                        //     {
                        //         Console.WriteLine($"User {userIndex}: Warning - No selected cards returned from API");
                        //         return;
                        //     }

                        //     selectedCard = selectedCards[0];
                        //     Console.WriteLine($"User {userIndex}: Selected card loaded with ID {selectedCard.Id}");
                        // }
                        // catch (Exception ex)
                        // {
                        //     Console.WriteLine($"User {userIndex}: Error getting selected card: {ex.Message}");
                        // }
                        selectedCard = cards[0];
                    }
                });

                // Handle "NumberSelected" event (user selects a number)
                connection.On<int>("NumberSelected", async (number) =>
                {
                    if (selectedCard == null) return;

                    // Check if this number has already been selected by this user
                    bool alreadySelected = false;
                    lock (selectedNumbersLock)
                    {
                        // Initialize the set if it doesn't exist for this user
                        if (!userSelectedNumbers.ContainsKey(userData.Id))
                        {
                            userSelectedNumbers[userData.Id] = new HashSet<int>();
                        }

                        // Check if this number has already been selected
                        if (userSelectedNumbers[userData.Id].Contains(number))
                        {
                            alreadySelected = true;
                            Console.WriteLine($"User {userIndex}: Number {number} already selected previously");
                        }
                    }

                    // Skip if already selected
                    if (alreadySelected) return;

                    // Flatten the 2D array of numbers
                    var flatNumbers = selectedCard.Cards.SelectMany(row => row).ToList();

                    if (flatNumbers.Contains(number))
                    {
                        int score = 10; // or based on some logic
                        var random = new Random();
                        var randomDelay = random.Next(100); // Random delay up to 100ms

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

                            // Mark this number as selected for this user
                            lock (selectedNumbersLock)
                            {
                                userSelectedNumbers[userData.Id].Add(number);
                            }

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
                await Task.Delay(TimeSpan.FromHours(24));
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