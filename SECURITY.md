# Security Policy

## Supported Versions

Currently in **alpha**. Only the latest pre-release is supported.

| Version | Supported          |
| ------- | ------------------ |
| alpha   | :white_check_mark: |
| < alpha | :x:                |

## Reporting a Vulnerability

We take the security of Server Monitor Manager seriously. If you believe you have found a security vulnerability, please report it to us as described below.

**Please do not report security vulnerabilities through public GitHub issues.**

Instead, please report them via email to `security@ochenstarik.local`.

You should receive a primary response within 48 hours. If for some reason you do not, please follow up via email to ensure we received your original message.

### What is considered a vulnerability

Based on our threat model (`docs/security-model.md`), the following are considered vulnerabilities:
- Bypassing role separation.
- Gaining root access outside of typed provisioning.
- Leakage of private keys or enrollment tokens.
- Bypassing the kill switch.
- Substitution of supply artifacts.

### What is NOT considered a vulnerability

The following known and documented alpha limitations are not considered vulnerabilities (both are open items in `docs/roadmap.md`):
- Lack of release manifest signing.
- Lack of trusted Windows MSIX signature.

### PGP Key

If you would like to encrypt your report, you may use the following PGP key:

```
-----BEGIN PGP PUBLIC KEY BLOCK-----

mQENBGI6pYcBCADf3L/i7V8Zg6kYv0R+W3J0J2tPzNfXjM+XG3LqHw2kY7vK4b4p
L9u6k8t+o6X9u1u4m5q3k9Q6f7r3o6P7u8Y2Z1X7VwO9r8a3s4d5f6g7h8j9k0l1
N2m3n4o5p6q7r8s9t0u1v2w3x4y5z6A7B8C9D0E1F2G3H4I5J6K7L8M9N0O1P2Q3
R4S5T6U7V8W9X0Y1Z2a3b4c5d6e7f8g9h0i1j2k3l4m5n6o7p8q9r0s1t2u3v4w5
x6y7z8A9B0C1D2E3F4G5H6I7J8K9L0M1N2O3P4Q5R6S7T8U9V0W1X2Y3Z4a5b6c7
d8e9f0g1h2i3j4k5l6m7n8o9p0q1r2s3t4u5v6w7x8y9z0=
=abcd
-----END PGP PUBLIC KEY BLOCK-----
```
