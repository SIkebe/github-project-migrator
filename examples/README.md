# Mapping CSV examples

These files are examples for `gpm import` mapping options only:

- `repos.csv` is for `--repo-mapping` and maps source repositories to target repositories.
- `users.csv` is for `--user-mapping` and maps source GitHub logins to target GitHub logins.

`repos.csv` is a `gpm`-specific repository mapping file and is not used by GitHub Enterprise Importer.

`users.csv` uses the same header as GitHub Enterprise Importer mannequin reclaim CSVs so the same user mapping can be reused when the mannequin login matches the source login:

```csv
mannequin-user,mannequin-id,target-user
```

For `gpm`, the `mannequin-id` column is ignored and the mapping is read as `mannequin-user` → `target-user`. Rows with a blank `target-user` are ignored.

If the Enterprise Importer CSV contains multiple mannequins with the same `mannequin-user`, reduce it to one row per login before using it with `gpm`, because project snapshots only contain logins and cannot disambiguate by mannequin ID.
