# TypeSafe Arcade — WPF Board Games

[Bahasa Indonesia](board-games.id.md) · **English**

`TypeSafe.BoardGames` is a .NET 10 WPF application with a modern arcade UI and Jack — The Code Bender as Player 2.

## Player identity

| Game | Player 1 | Player 2 / Jack AI |
|---|---|---|
| Tic Tac Toe | **Cyan ✕** | **Orange ●** |
| Connect Four | **Cyan ✕** | **Orange ●** |
| Othello | **Light ●** | **Dark ●** |

The layout keeps a permanent legend in the sidebar, player cards above the board, and a scoreboard using the same shapes and colours, so the two sides are never confused.

## Playable games

- **Tic Tac Toe** — 3×3, with a win/block tactical AI.
- **Othello** — 8×8 board, legal move highlighting, disc flipping in all eight directions, passing, a final disc count, and corner-priority AI.
- **Connect Four** — 6×7 board, column gravity, four-in-a-row detection, and a tactical win/block AI.

## Why the AI is reliable

1. Every move is verified by the local engine before it is applied.
2. In simulator mode, or when the API key is empty, Jack uses a deterministic local strategy.
3. In live mode, TypeSafe may only choose from the list of legal moves — an illegal answer cannot be expressed.
4. If the API response is invalid or the call fails, the local tactical fallback takes over.

This is the same pattern as specimen M-01 in the [Jev Gallery](jev-gallery.en.md): constrain the criteria to what is legal, and correctness stops depending on the model behaving.

## Configuration

`TypeSafe.BoardGames/app.config.json`:

```json
{
  "ApiKey": "",
  "Endpoint": "https://api.typesafe.ai",
  "DefaultModel": "jev-latest",
  "UseSimulator": true,
  "PlayerName": "You",
  "EnemyName": "Jack AI"
}
```

## Running it

```bash
dotnet run --project TypeSafe.BoardGames
```

Windows only — the project targets `net10.0-windows` and uses WPF.

Created by **Gravicode Studios**, led by Kang Fadhil.
