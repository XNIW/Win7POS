# Historical ordinary 5000-row Admin database responses

`candidate-5000-responses.zip` was fetched unchanged from Admin commit
`3f2c6913088e12effb71c22c11b4213f9570f227`.
SHA256: `bf960ae9e9a044244e191993d386ffba2951f680303a4f24536d8a24ab7519be`.

The response manifest binds five actual Admin handler/isolated PostgreSQL
economic RPC replies to the exact C# `candidate-5000` request bodies from
Win7POS `efb93cd47fd6bead0326a292100a7d9aa9d510ce`. The declared outer lease/token
and dependency schema are synthetic. This is not a shared TEST or live run.

The real C# HTTP/sync service reingested all replies unchanged. It compared
5000 exact product mappings, 10000 exact price mappings, stock 1.25, retail
price 1200, purchase price 900, original immutable payloads and all-ACK closure.
The archive remains historical evidence; the newer UTC corpus has its own
separate archive and postchecks under `admin-db-bee0670`.
