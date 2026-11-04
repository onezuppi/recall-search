# Security

## How to report a vulnerability

Do not open a public issue. Use a [private vulnerability report](https://github.com/onezuppi/recall-search/security/advisories/new) on GitHub. Describe what you found, how to reproduce it and what the impact is.

The author replies as soon as possible. The project does not promise fix times.

## What counts as a vulnerability

- Access to the index or to agent answers from another user account.
- Replacing the agent or the pipe so that the extension accepts a foreign answer.
- Reading files from the secrets list (`.pem`, `.key`, `.env`, `.kdbx` and others), or their text reaching the index.
- Indexing files outside the selected folders.

## Threat model

The project is meant for one user on a personal computer. The index holds the text of your documents and is not encrypted. File system permissions of your profile protect it. An attacker with access to your profile also has access to the index.
