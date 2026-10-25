# SAMPLE - read it, change the names, then run the lines you need (as Administrator, on the FILE server).
# The portal never gives extra rights: it acts as the signed-in user, so NTFS decides everything.

$domain = "TEST"            # NetBIOS name of the domain
$root   = "D:\Data"         # the folder (DFS target) used by the portal location

# 1) top-level folders must NOT inherit "Authenticated Users: Modify" from the drive root
icacls "$root" /inheritance:r
icacls "$root" /grant "SYSTEM:(OI)(CI)F" "BUILTIN\Administrators:(OI)(CI)F"
icacls "$root" /grant "${domain}\Domain Users:(RX)"          # this folder only: users can list the top level

# 2) per-folder rules:  (OI)(CI)RX = read only,  (OI)(CI)M = read + write
icacls "$root\Public" /grant "${domain}\Domain Users:(OI)(CI)M"
icacls "$root\HR"     /inheritance:r
icacls "$root\HR"     /grant "BUILTIN\Administrators:(OI)(CI)F" "SYSTEM:(OI)(CI)F"
icacls "$root\HR"     /grant "${domain}\HR-Read:(OI)(CI)RX" "${domain}\HR-Write:(OI)(CI)M"

# 3) RECYCLE BIN (needed once per location root): users may ADD items but not list or read them
New-Item -ItemType Directory "$root\.recycle" -Force | Out-Null
attrib +h "$root\.recycle"
icacls "$root\.recycle" /inheritance:r
icacls "$root\.recycle" /grant "SYSTEM:(OI)(CI)F" "BUILTIN\Administrators:(OI)(CI)F"
icacls "$root\.recycle" /grant "${domain}\Domain Users:(WD,AD)"   # add file / add folder, this folder only
# automatic clean-up runs as the IIS application pool identity (service account or the web server computer account):
# icacls "$root\.recycle" /grant "${domain}\WEBSERVER$:(OI)(CI)M"
