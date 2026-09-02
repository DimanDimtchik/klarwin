#!/usr/bin/env python3
"""Upload KlarWin package files via FTP (temporary KAS FTP user).

Only overwrites the listed files. Never deletes the remote folder or extra
files there — that directory is user/instance content.

Usage:
  python deploy/ftp-upload-klarwin.py
"""
from __future__ import annotations

import ftplib
import json
import re
import time
from pathlib import Path

from lxml import etree
from zeep import Client
from zeep.settings import Settings

ROOT = Path(__file__).resolve().parents[1]
KAS_PHP = Path(r"C:\Users\dietr\Projects\DG\config\kas.local.php")
LOCAL_DIR = ROOT / "deploy" / "dg.ganz-soft.de" / "klarwin"
FTP_HOST = "w0217246.kasserver.com"
REMOTES = [
    "/dg.ganz-soft.de/klarwin",
    "/ganz-soft.de/klarwin",
]
FILES = [
    "KlarWin-Setup.zip",
    "version.json",
    "index.html",
    "KlarWin-Handbuch.pdf",
    "KlarWin-Handbuch.html",
    "Installieren.bat",
]


def load_kas() -> tuple[str, str, str]:
    raw = KAS_PHP.read_text(encoding="utf-8")
    login = re.search(r"['\"]kas_login['\"]\s*=>\s*['\"]([^'\"]+)['\"]", raw)
    auth = re.search(r"['\"]kas_auth_data['\"]\s*=>\s*['\"]([^'\"]+)['\"]", raw)
    typ = re.search(r"['\"]kas_auth_type['\"]\s*=>\s*['\"]([^'\"]+)['\"]", raw)
    if not login or not auth:
        raise SystemExit("kas.local.php parse failed")
    return login.group(1), auth.group(1), (typ.group(1) if typ else "plain")


def kas_xml(client: Client, login: str, password: str, auth_type: str, action: str, params: dict | None = None) -> str:
    payload = {
        "kas_login": login,
        "kas_auth_type": auth_type,
        "kas_auth_data": password,
        "kas_action": action,
        "KasRequestParams": params or {},
    }
    result = client.service.KasApi(json.dumps(payload))
    time.sleep(2)
    vals = getattr(result, "__values__", {})
    raw_elems = vals.get("_raw_elements")
    if not raw_elems:
        return ""
    return etree.tostring(list(raw_elems)[0], encoding="unicode")


def mkdir_p(ftp: ftplib.FTP, path: str) -> None:
    cur = ""
    for part in path.strip("/").split("/"):
        cur += "/" + part
        try:
            ftp.mkd(cur)
        except ftplib.error_perm:
            pass


def main() -> int:
    zip_path = LOCAL_DIR / "KlarWin-Setup.zip"
    release = ROOT / "dist" / "release" / "KlarWin-Setup.zip"
    if not zip_path.exists() and release.exists():
        zip_path.write_bytes(release.read_bytes())
    if not zip_path.exists():
        raise SystemExit("KlarWin-Setup.zip missing")

    login, password, auth_type = load_kas()
    client = Client(
        "https://kasapi.kasserver.com/soap/wsdl/KasApi.wsdl",
        settings=Settings(strict=False, xml_huge_tree=True),
    )
    ftp_pass = "KlarWinTmp9Upload!"
    xml = kas_xml(
        client,
        login,
        password,
        auth_type,
        "add_ftpuser",
        {
            "ftp_password": ftp_pass,
            "ftp_path": "/",
            "ftp_comment": "klarwin-upload",
            "ftp_permission_write": "Y",
        },
    )
    m = re.search(r"<key[^>]*>ReturnInfo</key><value[^>]*>([^<]+)</value>", xml)
    ftp_user = m.group(1) if m else ""
    if not ftp_user:
        raise SystemExit("could not create FTP user: " + xml[:400])
    print("FTP user", ftp_user)

    ftp = ftplib.FTP()
    ftp.connect(FTP_HOST, 21, timeout=90)
    ftp.login(ftp_user, ftp_pass)
    for remote in REMOTES:
        print("=>", remote)
        mkdir_p(ftp, remote)
        ftp.cwd(remote)
        for name in FILES:
            local = LOCAL_DIR / name
            if not local.exists():
                continue
            print("upload", name)
            with local.open("rb") as fh:
                ftp.storbinary(f"STOR {name}", fh)
    ftp.quit()
    print("UPLOAD_DONE")

    time.sleep(3)
    try:
        kas_xml(client, login, password, auth_type, "delete_ftpuser", {"ftp_login": ftp_user})
        print("temp FTP user deleted")
    except Exception as ex:
        print("delete later:", ex)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
