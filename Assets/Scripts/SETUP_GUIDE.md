# Gameplay Implementation Setup Guide

## New Scripts to Add to Unity Project
1. `GameState.cs`
2. `GameManager.cs`
3. `EventManager.InGame.cs`
4. `InGameView.cs` (replace existing)
5. `InGameManager.cs` (replace existing — CurrentRoom property added)

## Step 1 — GameObject Setup

### GameManager
- Add `GameManager` component to your DontDestroyOnLoad GO
  (same GO as FirebaseManager, MatchmakingManager, InGameManager)

### FlippedCards GO (Shootout)
- Create GO named `FlippedCards` in the InGame scene (center of table)
- Add GridLayoutGroup or HorizontalLayoutGroup
- Set inactive by default
- Wire to `InGameView.flippedCardsContainer`

### FlippedCard Prefab
- Create new GO → add `Image` + `Button` components
- Image = back-of-card sprite
- Save as prefab named `FlippedCard`
- Wire to `InGameView.flippedCardPrefab`

### Card Prefab Update
- Your existing Card prefab needs a `Button` component added to root
- Set Button's Disabled color to grey (so non-playable cards grey out)
- Transition: Color Tint, Disabled Color: grey with ~60% alpha

## Step 2 — ProfileSlot Inspector Wiring (InGameView)

Each of the 4 profiles needs these fields wired:

| Field               | Source in Hierarchy                          |
|---------------------|----------------------------------------------|
| avatarImage         | Profile/ProfileBG/AvatarBG/Avatar (Image)   |
| nameText            | Profile/NameTxt (Text)                       |
| remainingCardsText  | Profile/RemainigCards/RemainingCardTxt (Text)|
| playedCardImage     | Profile/PlayedCardImage (Image) ← NEW GO    |
| timerImage          | Profile/TurnTimerBG/ImageSliderFillAmountBased (Image) |
| stealButton         | Profile/StealBtn (Button)                    |
| resultText          | Profile/ResultTxt (Text)                     |

**Create for each Profile:**
- `PlayedCardImage` — new child GO with Image component, set inactive by default
- `StealBtn` — new child GO with Button + Text, set inactive by default  
  (Only Profile/seat 0 = local player's steal button will ever be shown)
- `ResultTxt` — new child GO with Text component, set inactive by default

## Step 3 — Timer Image Setup

`ImageSliderFillAmountBased` is your turn timer:
- Image Type: **Filled**
- Fill Method: **Radial 360** (or Horizontal — your choice)
- Fill Origin: Top
- Fill Amount: starts at 1, drains to 0

GameManager drives `timerImage.fillAmount` each frame via `OnTimerTick`.

## Step 4 — PlayFabManager Update

Open `PlayFabManager.cs` and add the contents of `PlayFabManager_GameWin_Addition.cs`:
1. Subscribe/unsubscribe `HandleAwardGameWinCoins` in OnEnable/OnDisable
2. Add the method body

## Step 5 — Firestore Room Document Structure

The room document now has an additional `gameState` field written by host
when game starts. Structure:
```
rooms/{roomId}: {
  hostId, entryFee, status, players, hands,   ← existing
  gameState: {
    currentPlayerIndex, leadSuit, cardsInPlay,
    phase, winners, bhabhi, roundNumber,
    turnStartTime, shootoutDrawerId, shootoutResponderId
  }
}
```

No new Firestore collections needed.

## Step 6 — Firestore Rules (for testing)
```
rules_version = '2';
service cloud.firestore {
  match /databases/{database}/documents {
    match /rooms/{roomId} {
      allow read, write: if true;  // test mode
    }
  }
}
```

## Step 7 — Turn Flow Summary

```
Host deals cards → writes gameState to Firestore
    ↓
All clients start listener
    ↓
Each client sees currentPlayerIndex
    ↓
If it's MY id → EventManager.FireMyTurnStarted → cards become clickable
If it's a BOT id (host only) → BotTurnRoutine starts
If it's other real player → wait for Firestore update
    ↓
Timer runs locally (20s) — only turnStartTime was written once
    ↓
Card played → SubmitCardPlay → removes from hand, adds to cardsInPlay
    ↓
If round complete → ResolveRound → StartNextRound → write gameState
    ↓
Repeat until 1 player left → Bhabhi → game finished → award coins
```

## Step 8 — Steal Button Logic

- Steal button sits under Profile GO (seat 0 = local player only)
- Shown only when: it's your turn AND you're leading AND >2 active players
  AND left player hasn't won yet
- `GameManager.CanSteal()` checks all conditions
- `GameManager.StealLeftPlayerHand()` executes the steal

## Known Shoot-out Flow

```
2 players left
Drawer plays last card → responder plays same suit but LOWER value
→ Cards discarded, drawer has 0 cards but game not over
→ CheckAndTriggerShootout fires
→ FlippedCards shown (count = responder's hand size)
→ Drawer picks any card (or auto-pick after 10s)
→ Random card drawn from responder's hand
→ Drawer must play that card (20s)
→ Responder follows suit or plays thulla
→ Normal round resolution determines Bhabhi
```
