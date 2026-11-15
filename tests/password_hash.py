import base64
import hashlib
import os

password = os.environ["POS_TEST_LOCAL_PASSWORD"].encode()
salt = os.urandom(16)
digest = hashlib.pbkdf2_hmac("sha256", password, salt, 210_000, 32)
print("pbkdf2-sha256$210000$" + base64.b64encode(salt).decode() + "$" + base64.b64encode(digest).decode())
