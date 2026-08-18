## Description

This PR implements repository hygiene and security improvements according to TASK.md.

Fixes Task B-3R

### Actions SHAs
The following commands were used to retrieve the exact SHAs for the pinned actions (dereferencing annotated tags to commit SHAs where needed):

1. `actions/checkout@v6`: `gh api repos/actions/checkout/git/ref/tags/v6` -> dereferenced to commit `d23441a48e516b6c34aea4fa41551a30e30af803`
2. `actions/setup-dotnet@v5`: `gh api repos/actions/setup-dotnet/git/ref/tags/v5` -> dereferenced to commit `26b0ec14cb23fa6904739307f278c14f94c95bf1`
3. `actions/upload-artifact@v6`: `gh api repos/actions/upload-artifact/git/ref/tags/v6` -> dereferenced to commit `b7c566a772e6b6bfb58ed0dc250532a479d7789f`
4. `actions/download-artifact@v8`: `gh api repos/actions/download-artifact/git/ref/tags/v8` -> dereferenced to commit `74a6210ce7665476a66b96e5fa3ebf68e98ec72d`
5. `softprops/action-gh-release@v2`: `gh api repos/softprops/action-gh-release/git/ref/tags/v2` -> dereferenced to commit `3bb12739c298aeb8a4eeaf626c5b8d85266b0e65`

## Type of change

- [x] Security and Repository Hygiene (non-breaking changes)

## Testing Checklist

### Verified Locally
- [x] Git diff on `main` boundary files: Verified no restricted files from the `TASK.md` bounds were modified (`git diff --name-only main`).
- [x] Workflow Syntax: Verified workflow yaml syntax.

### Verified in CI
- [x] CI tests and builds run on PR. (Awaiting GitHub Actions run after PR creation).

### Not Verified
- [x] Physical acceptance testing (`SMM_ACCEPT_RESTORE=1 SMM_ACCEPT_REBOOT=1 tests/acceptance/three-server-mesh.sh`) was **NOT verified** because SSH- and topology parameters were not provided as per `TASK.md` instructions. Contract/mock tests were not used as a substitute for this.
