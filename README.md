# AWS POP3 Server
## Background
AWS SES can be configured to receive emails on your behalf.  SES will save incoming emails to AWS S3 and alert an AWS Lambda function that it has done so.  The problem that this application tries to solve is getting those emails out of S3 in a way that is less painful way.  This project attempts to solve this problem by creating a POP3 server that runs on localhost and fetches the emails stored on AWS using the ```AWS SDK for .NET```.

## Setup
AWS S3 and SES both have to be configured in a very specific way for this application to perform properly.  First, the lambda function must be created and given permission to access S3.  This function will serve as a sorter by breaking the recipients down by ```user``` and ```domain``` and the resulting file structure will look something like the example below.

```
Bucket/mailboxes/foo.com/passwd.txt
Bucket/mailboxes/foo.com/tom/
Bucket/mailboxes/foo.com/dick/

Bucket/mailboxes/bar.org/passwd.txt
Bucket/mailboxes/bar.org/harry/
Bucket/mailboxes/bar.org/jane/

Bucket/mailboxes/boo.net/passwd.txt
Bucket/mailboxes/boo.net/harry/
Bucket/mailboxes/boo.net/jane/
```

### Password Store
The password store for each domain should be placed in the root of the domain folder.  The password should be salted with the username and hashed using SHA-512.  The username and password hash must be seperated with a colon.
```
sha512("admin:password") -> hashed password (see below)
admin:601f8f22b15321a3cd342c1d50c6ce8da153da970d3dbfe25b3dbfa9326c3b30ac32d8725d8513fc43a5e7a97f637c502a0a1f05c997ebf4de7729676dba56d2
```

#### Lambda function
```
import boto3
import json
import os

bucket = "<YOUR BUCKET NAME HERE>"
prefix = "mailboxes/" #this can be changed to anything
s3 = boto3.client("s3")

def lambda_handler(event, context):
	message = event["Records"][0]["ses"]
	message_id = message["mail"]["messageId"]
	recipients = message["receipt"]["recipients"]

	for recipient in recipients:
		parts = recipient.split("@")
		if(len(parts) == 2):
			domainname = parts[1]
			username_parts = parts[0].split("+")
			username = username_parts[0]
			
			copy_source = { "Bucket": bucket, "Key": message_id}
			new_key = prefix + domainname + "/" + username + "/" + message_id
			s3.copy(copy_source, bucket, new_key)
	
	s3.delete_object(Bucket=bucket, Key=message_id)
```

#### Policy
```
{
    "Version": "2012-10-17",
    "Statement": [
        {
            "Sid": "VisualEditor0",
            "Effect": "Allow",
            "Action": [
                "logs:CreateLogStream",
                "logs:CreateLogGroup",
                "logs:PutLogEvents"
            ],
            "Resource": "*"
        },
        {
            "Sid": "VisualEditor1",
            "Effect": "Allow",
            "Action": [
                "s3:PutObject",
                "s3:GetObject",
                "s3:ListBucket",
                "s3:DeleteObject",
                "s3:HeadBucket"
            ],
            "Resource": [
                "arn:aws:s3:::<YOUR BUCKET NAME HERE>/*",
                "arn:aws:s3:::<YOUR BUCKET NAME HERE>"
            ]
        }
    ]
}
```

Once AWS is configured, run the application and setup POP3 inside your favorite client like you always would.