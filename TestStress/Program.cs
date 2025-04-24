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
using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.SignalR.Client;
using Newtonsoft.Json;

namespace BingoSignalRClient
{
    class Program
    {
        private const string BASE_URL = "https://bingo-backend.zetabox.tn";
        //private const int CARD_DELAY = 100; // 1 hour
        //private const int SELECT_DELAY = 100; // 1 hour
        private static int MAX_TIMER = int.Parse(Environment.GetEnvironmentVariable("MAX_TIMER"));
        private static int USER_DELAY = int.Parse(Environment.GetEnvironmentVariable("delay"));
        private static int semaphore = int.Parse(Environment.GetEnvironmentVariable("semaphore"));

        private static int fail = 0;
        private static int notif = 0;
        private static readonly object lockObject = new object();

        // Track card IDs to detect duplicates across users (needs to be shared)
        private static readonly Dictionary<int, int> cardIdToUserMap = new Dictionary<int, int>();
        private static readonly object cardMapLock = new object();

        // Semaphore to limit concurrent card operations to 500 at a time
        private static readonly SemaphoreSlim cardOperationsSemaphore = new SemaphoreSlim(semaphore, semaphore);

        // Static flag to control whether distribution should proceed
        private static bool allowDistribution = false;

        // List to store user tokens loaded from file
        private static List<UserToken> userTokens = new List<UserToken>();

        // Path to the tokens file (default value, can be overridden by command-line argument)
        private static string tokensFilePath = "tokens.json";

        static async Task Main(string[] args)
        {
            Console.WriteLine("Starting Bingo SignalR Client Simulation");

            // Check if a tokens file was specified as a command-line argument
            if (args.Length > 0)
            {
                tokensFilePath = args[0];
                Console.WriteLine($"Using tokens file: {tokensFilePath}");
            }
            else
            {
                Console.WriteLine($"No tokens file specified, using default: {tokensFilePath}");
            }

            // Load tokens from file
            LoadTokensFromFile();

            int userCount = userTokens.Count;
            Console.WriteLine($"Loaded {userCount} user tokens from file");

            if (userCount == 0)
            {
                Console.WriteLine("No tokens found in the file. Please add tokens to tokens.json");
                return;
            }

            // Create a list to hold all user simulation tasks
            var tasks = new List<Task>();

            for (int i = 0; i < userCount; i++)
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
                await Task.Delay(USER_DELAY);

                lock (lockObject)
                {
                    Console.WriteLine($"Fail: {fail}");
                    Console.WriteLine($"Notifications: {notif}");
                }
            }

            // Start a separate task to wait for user input to resume distribution
            Task.Run(() =>
            {
                Console.WriteLine("\nPress Enter to allow card distribution to proceed when the 'distribution_in_progress' event occurs...");
                Console.ReadLine();
                Console.WriteLine("\n*** DISTRIBUTION UNPAUSED - All threads will now proceed with card operations ***\n");
                allowDistribution = true; // Set the flag to allow distribution to proceed
            });

            // Wait for all tasks to complete
            await Task.WhenAll(tasks);
            Console.WriteLine("Simulation completed");
        }

        // Load tokens from the JSON file
        private static void LoadTokensFromFile()
        {
            try
            {
                string filePath = Path.Combine(Directory.GetCurrentDirectory(), tokensFilePath);
                if (File.Exists(filePath))
                {
                    string json = File.ReadAllText(filePath);

                    // Parse tokens as a simple array of strings
                    var tokenStrings = JsonConvert.DeserializeObject<List<string>>(json);

                    if (tokenStrings != null && tokenStrings.Count > 0)
                    {
                        Console.WriteLine($"Found {tokenStrings.Count} tokens in file");

                        foreach (var tokenString in tokenStrings)
                        {
                            try
                            {
                                // Parse the JWT token to extract the user ID
                                var handler = new JwtSecurityTokenHandler();
                                var token = handler.ReadJwtToken(tokenString);

                                // Try to get the user ID from the token claims
                                var userIdClaim = token.Claims.FirstOrDefault(c => c.Type == "IdUser");

                                if (userIdClaim != null && int.TryParse(userIdClaim.Value, out int userId))
                                {
                                    userTokens.Add(new UserToken
                                    {
                                        UserId = userId,
                                        AccessToken = tokenString
                                    });
                                }
                                else
                                {
                                    Console.WriteLine($"Warning: Could not extract user ID from token: {tokenString.Substring(0, 20)}...");
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"Error parsing token: {ex.Message}");
                            }
                        }
                    }
                    else
                    {
                        Console.WriteLine("No tokens found in the file or invalid format");
                    }
                }
                else
                {
                    Console.WriteLine($"Tokens file not found at {filePath}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading tokens from file: {ex.Message}");
            }
        }

        // Calculate score for a selected number based on the card and currently selected numbers
        private static int CalculateScore(Card card, HashSet<int> selectedNumbers, int newNumber)
        {
            // For regular number selection, we'll use all conditions
            string condition = "line,column,diag";
            return CalculateScore(card, selectedNumbers, newNumber, condition);
        }

        // Calculate score for a selected number based on the card, selected numbers, and specific condition
        private static int CalculateScore(Card card, HashSet<int> selectedNumbers, int newNumber, string condition)
        {
            var cardGrid = card.Cards;
            int size = cardGrid.Count;
            int totalScore = 0;

            // Helper function to count and score a line (row, column, or diagonal)
            int CountAndScoreLine(List<int> line)
            {
                // Get numbers in the line that have been drawn (including the new number)
                var tempSelectedNumbers = new HashSet<int>(selectedNumbers);
                tempSelectedNumbers.Add(newNumber);
                var drawnInLine = line.Where(num => tempSelectedNumbers.Contains(num)).ToList();
                int lineScore = 0;

                // Calculate score based on position
                for (int i = 0; i < drawnInLine.Count; i++)
                {
                    lineScore += (i + 1) * 10;
                }

                return lineScore;
            }

            // Check rows (lines)
            if (condition.Contains("line"))
            {
                foreach (var row in cardGrid)
                {
                    totalScore += CountAndScoreLine(row);
                }
            }

            // Check columns
            if (condition.Contains("column"))
            {
                for (int col = 0; col < size; col++)
                {
                    var column = cardGrid.Select(row => row[col]).ToList();
                    totalScore += CountAndScoreLine(column);
                }
            }

            // Check diagonals if it's a square grid
            if (condition.Contains("diag") && size > 0 && cardGrid[0].Count == size)
            {
                // Diagonal 1 (top-left to bottom-right)
                var diag1 = new List<int>();
                for (int i = 0; i < size; i++)
                {
                    diag1.Add(cardGrid[i][i]);
                }
                totalScore += CountAndScoreLine(diag1);

                // Diagonal 2 (top-right to bottom-left)
                var diag2 = new List<int>();
                for (int i = 0; i < size; i++)
                {
                    diag2.Add(cardGrid[i][size - i - 1]);
                }
                totalScore += CountAndScoreLine(diag2);
            }

            // Check full card
            if (condition.Contains("card"))
            {
                var allNumbers = cardGrid.SelectMany(row => row).ToList();
                var tempSelectedForCard = new HashSet<int>(selectedNumbers);
                tempSelectedForCard.Add(newNumber);
                var drawnInCard = allNumbers.Where(num => tempSelectedForCard.Contains(num)).ToList();

                for (int i = 0; i < drawnInCard.Count; i++)
                {
                    totalScore += (i + 1) * 10;
                }
            }

            // Log the score calculation for debugging
            Console.WriteLine($"Score calculated: {totalScore} for number {newNumber} with condition {condition}");

            return totalScore;
        }

        // Check for bingo winning conditions and declare winners if conditions are met
        private static async Task CheckAndDeclareWinningConditions(HttpClient httpClient, Card card, HashSet<int> selectedNumbers, int userIndex)
        {
            try
            {
                // Initialize winning conditions
                bool winnerWithLine = false;
                bool winnerWithColumn = false;
                bool winnerWithDiagonal = false;
                bool winnerWithAllCarte = false;

                // Get the card grid
                var cardGrid = card.Cards;
                int rows = cardGrid.Count;
                int cols = rows > 0 ? cardGrid[0].Count : 0;

                if (rows == 0 || cols == 0)
                {
                    Console.WriteLine($"User {userIndex}: Invalid card format, cannot check winning conditions");
                    return;
                }

                // Check for line win (complete row)
                for (int i = 0; i < rows; i++)
                {
                    bool rowComplete = true;
                    for (int j = 0; j < cols; j++)
                    {
                        if (!selectedNumbers.Contains(cardGrid[i][j]))
                        {
                            rowComplete = false;
                            break;
                        }
                    }
                    if (rowComplete)
                    {
                        winnerWithLine = true;
                        Console.WriteLine($"User {userIndex}: Completed a line (row {i})");
                        break;
                    }
                }

                // Check for column win (complete column)
                for (int j = 0; j < cols; j++)
                {
                    bool colComplete = true;
                    for (int i = 0; i < rows; i++)
                    {
                        if (!selectedNumbers.Contains(cardGrid[i][j]))
                        {
                            colComplete = false;
                            break;
                        }
                    }
                    if (colComplete)
                    {
                        winnerWithColumn = true;
                        Console.WriteLine($"User {userIndex}: Completed a column (column {j})");
                        break;
                    }
                }

                // Check for diagonal win (top-left to bottom-right)
                if (rows == cols) // Only check diagonal if it's a square grid
                {
                    bool diag1Complete = true;
                    for (int i = 0; i < rows; i++)
                    {
                        if (!selectedNumbers.Contains(cardGrid[i][i]))
                        {
                            diag1Complete = false;
                            break;
                        }
                    }

                    // Check for diagonal win (top-right to bottom-left)
                    bool diag2Complete = true;
                    for (int i = 0; i < rows; i++)
                    {
                        if (!selectedNumbers.Contains(cardGrid[i][cols - 1 - i]))
                        {
                            diag2Complete = false;
                            break;
                        }
                    }

                    if (diag1Complete || diag2Complete)
                    {
                        winnerWithDiagonal = true;
                        Console.WriteLine($"User {userIndex}: Completed a diagonal");
                    }
                }

                // Check for full card win
                bool allNumbersSelected = true;
                for (int i = 0; i < rows; i++)
                {
                    for (int j = 0; j < cols; j++)
                    {
                        if (!selectedNumbers.Contains(cardGrid[i][j]))
                        {
                            allNumbersSelected = false;
                            break;
                        }
                    }
                    if (!allNumbersSelected)
                        break;
                }

                if (allNumbersSelected)
                {
                    winnerWithAllCarte = true;
                    Console.WriteLine($"User {userIndex}: Completed the entire card!");
                }

                // If any winning condition is met, call the winners API
                if (winnerWithLine || winnerWithColumn || winnerWithDiagonal || winnerWithAllCarte)
                {
                    var winnerData = new
                    {
                        winnerWithLine = winnerWithLine,
                        winnerWithColumn = winnerWithColumn,
                        winnerWithDiagonal = winnerWithDiagonal,
                        winnerWithAllCarte = winnerWithAllCarte
                    };

                    var winnerContent = new StringContent(
                        JsonConvert.SerializeObject(winnerData),
                        Encoding.UTF8,
                        "application/json"
                    );

                    Console.WriteLine($"User {userIndex}: Declaring bingo win with conditions: Line={winnerWithLine}, Column={winnerWithColumn}, Diagonal={winnerWithDiagonal}, AllCard={winnerWithAllCarte}");

                    var winnerResponse = await httpClient.PostAsync($"{BASE_URL}/api/winners", winnerContent);
                    winnerResponse.EnsureSuccessStatusCode();

                    Console.WriteLine($"User {userIndex}: Successfully declared bingo win!");
                }
                else
                {
                    Console.WriteLine($"User {userIndex}: No winning conditions met yet");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"User {userIndex}: Error checking or declaring winning conditions: {ex.Message}");
            }
        }

        private static async Task SimulateUser(int userIndex)
        {
            // Thread-specific collections for this user
            var userSelectedNumbers = new HashSet<int>();
            var pendingNumberSelections = new HashSet<int>();

            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            try
            {
                // Get the user token from the loaded list
                if (userIndex >= userTokens.Count)
                {
                    throw new Exception($"User index {userIndex} exceeds available tokens count {userTokens.Count}");
                }

                var userToken = userTokens[userIndex];
                int userId = userToken.UserId;
                string token = userToken.AccessToken;

                Console.WriteLine($"User {userIndex}: Starting with userId {userId} and token...");

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

                        //var randomTime = new Random();
                        //var randomDelayTime = randomTime.Next(CARD_DELAY); // Random delay up to 1 hour

                        // Use Task.Delay instead of setTimeout
                        //await Task.Delay(randomDelayTime);

                        // Wait for user input before proceeding with distribution
                        Console.WriteLine($"User {userIndex}: Waiting for user input to proceed with card operations...");

                        // Poll the allowDistribution flag until it becomes true
                        while (!allowDistribution)
                        {
                            await Task.Delay(100); // Check every 100ms
                        }

                        // Wait for semaphore to limit concurrent card operations
                        await cardOperationsSemaphore.WaitAsync();
                        Console.WriteLine($"User {userIndex}: Acquired semaphore for card operations");

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
                                            tempCardAssignments[card.Id] = userId;
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
                                        Console.WriteLine($"WARNING: User {userIndex} with user ID {userId}: Received duplicate cards! {duplicateDetails} Retrying ({retryCount}/{maxRetries})...");
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
                                await Task.Delay(50); // Wait a bit longer after an error
                            }
                        }

                        if (!foundUniqueCards)
                        {
                            Console.WriteLine($"ERROR: User {userIndex} with user ID {userId}: Could not get unique cards after {maxRetries} retries. Proceeding with potentially duplicate cards.");

                            // As a last resort, record these cards as assigned to this user
                            lock (cardMapLock)
                            {
                                foreach (var card in cards ?? new List<Card>())
                                {
                                    cardIdToUserMap[card.Id] = userId;
                                }
                            }
                        }

                        Console.WriteLine($"User {userIndex} with user ID {userId}: Got {cards?.Count ?? 0} cards with ids: {string.Join(", ", cards?.Select(c => c.Id) ?? new List<int>())}");

                        if (cards?.Count > 0)
                        {
                            var selectedId = cards[0].Id; // Select the first card for simplicity
                                                          //var random = new Random();
                                                          //var randomDelay = random.Next(SELECT_DELAY); // Random delay up to 1 hour

                            // Use Task.Delay instead of setTimeout
                            //await Task.Delay(randomDelay);

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

                                Console.WriteLine($"User {userIndex}: Card selected");
                                Interlocked.Increment(ref notif);
                                cardSelected = true;
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"User {userIndex}: Failed to select card: {ex.Message}");
                            }
                        }

                        // Release the semaphore after all card operations are completed
                        cardOperationsSemaphore.Release();
                        Console.WriteLine($"User {userIndex}: Released semaphore for card operations");
                    }

                    if (status == "emission_in_progress")
                    {
                        selectedCard = cards[0];
                    }
                });

                // Handle "NumberSelected" event (user receives notification of a number to select)
                connection.On<int>("NumberSelected", (number) =>
                {
                    Console.WriteLine($"User {userIndex}: Received number {number} to select");

                    if (selectedCard == null) return;

                    // Check if this number is in the user's card
                    bool numberInCard = selectedCard.Cards.SelectMany(row => row).Contains(number);
                    if (!numberInCard)
                    {
                        Console.WriteLine($"User {userIndex}: Number {number} not in card");
                        return;
                    }

                    Console.WriteLine($"User {userIndex}: Number {number} is in card");

                    // Skip if already selected or pending
                    if (userSelectedNumbers.Contains(number))
                    {
                        Console.WriteLine($"User {userIndex}: Number {number} was already selected by this user");
                        return;
                    }

                    // Skip if already pending selection
                    if (pendingNumberSelections.Contains(number))
                    {
                        Console.WriteLine($"User {userIndex}: Number {number} is already pending selection");
                        return;
                    }

                    // Queue number for selection
                    pendingNumberSelections.Add(number);
                    Console.WriteLine($"User {userIndex}: Number {number} queued for selection during Timer event");
                });

                // Handle "Timer" event (game countdown)
                connection.On<int>("Timer", async (timeLeft) =>
                {
                    Console.WriteLine($"User {userIndex}: Timer = {timeLeft}");

                    // Only attempt to select numbers when we have a selected card
                    if (selectedCard != null)
                    {
                        // Check for bingo winning conditions when timer reaches 0
                        if (timeLeft == 0)
                        {
                            await CheckAndDeclareWinningConditions(httpClient, selectedCard, userSelectedNumbers, userIndex);
                        }
                        // Use timeLeft to distribute the load
                        // For example, some users will select at timeLeft = 10, others at 9, etc.
                        // This creates a more natural distribution based on the user's index
                        int userSpecificTriggerTime = (userIndex % MAX_TIMER) + 1; // Distribute across 1-5 seconds

                        if (timeLeft == userSpecificTriggerTime)
                        {
                            // Process any pending number selections
                            if (pendingNumberSelections.Count > 0)
                            {
                                // Add a random delay (max 900ms) before processing numbers
                                var random = new Random();
                                var initialDelay = random.Next(900); // Random delay up to 900ms
                                await Task.Delay(initialDelay);
                                // Make a copy of the pending numbers
                                var pendingNumbers = new HashSet<int>(pendingNumberSelections);
                                // Clear the pending selections as we're about to process them
                                pendingNumberSelections.Clear();

                                foreach (int numberToSelect in pendingNumbers)
                                {
                                    // Check if this number has already been selected
                                    bool alreadySelected = userSelectedNumbers.Contains(numberToSelect);

                                    if (!alreadySelected)
                                    {
                                        // Calculate score based on the card and currently selected numbers
                                        int score = CalculateScore(selectedCard, userSelectedNumbers, numberToSelect);

                                        try
                                        {
                                            var numberData = new[] { numberToSelect, score };
                                            var numberContent = new StringContent(
                                                JsonConvert.SerializeObject(numberData),
                                                Encoding.UTF8,
                                                "application/json"
                                            );

                                            var numberResponse = await httpClient.PostAsync($"{BASE_URL}/api/SelectedNumberClient/Number", numberContent);
                                            numberResponse.EnsureSuccessStatusCode();

                                            // Mark this number as selected for this user
                                            userSelectedNumbers.Add(numberToSelect);

                                            Console.WriteLine($"User {userIndex}: Sent selected number {numberToSelect} with score {score} at timeLeft={timeLeft}");
                                            Interlocked.Increment(ref notif);
                                        }
                                        catch (Exception ex)
                                        {
                                            Console.WriteLine($"User {userIndex}: Error sending selected number: {ex.Message}");
                                        }
                                    }
                                    else
                                    {
                                        Console.WriteLine($"User {userIndex}: Number {numberToSelect} already selected, skipping");
                                    }
                                }
                            }
                        }
                    }
                });

                // Step 6: Start SignalR connection
                await connection.StartAsync();
                connection.Closed += async (error) =>
                {
                    Console.WriteLine($"User {userIndex}: Connection closed: {error?.Message}");
                    await Task.Delay(2000);
                    await connection.StartAsync();
                };
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
    public class UserToken
    {
        [JsonProperty("userId")]
        public int UserId { get; set; }

        [JsonProperty("accessToken")]
        public string AccessToken { get; set; }
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