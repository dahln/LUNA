# 🌙 L.U.N.A. Master Deployment Blueprint

LUNA stands for: Local-Univeral-Neural-Agent

## Project Description

I am very interested in self-hosting AI, and have expiramented with Ollama. I've recently been expiramenting with OpenClaw and find the project very exciting! I wanted to see if I could create my own Agent tool, powered my Ollama. This project could run on any linux machine - but I wanted to see what type of experience I coudl get by running this on my Pi 5, 16gb ram, with an NVME m.2 drive. This is a great setup for a pi, but obviously there is no GPU and that effects the AI performace. But - with the agentic "fire and forget" flow, I wanted to see what happened. This project initially used OpenClaw, but I found the project overkill for my usage, and I wanted to see if I could build my own. So here is LUNA Agent: **This is a work in progress**. I've taken what I wanted to do with OpenClaw, and made them the defaults for LUNA. Slack for chat with the agent, GitHub intergration with Git and the GitHub CLI, Docker for sandboxing tasks. The idea is to chat with agent via slack, the AI will take those tasks, create a docker container sandbox, and work on it; it should be updates via slack. I've also built out commands for the AI via Slack. For kicks-and-giggle,s I've attached a small SSD1306 screen that can give output - but this isn't required and only works on a Gpio-capable device. 

I'm still working on the setup document and process, have deployed it and am actively testing it. I'm making the repo 'public' so it is easier to clone and pull updates from to the pi as I iterate over problems that need to be solved. I'm excited about this project and thought it would be fun to share! Disclaimer: This could be a total disaster.

A note on the programs. This is a .NET 10 project. I am using .NET 10 File-Based Apps - I love this new simple pattern. In my opinion it makes it very convinient to create simple programs that can run idependently or be chained together. 

You'll also find a .3mf file, a simple case I designed to pull air in and push air out - I wanted to ensure the pi had very active cooling (I also put an active-cooler on the board).


**Platform:** Raspberry Pi 5 (16GB RAM) + NVMe M.2 SSD  
**Stack:** .NET 10 (Native AOT), Ollama, LUNA Agent  
**Hostname:** LUNAHOST  
**User:** luna (non-root user)

## Important Disclaimer

**This project involves an AI agent with access to system resources, external APIs, and the ability to execute commands. While designed with security in mind (running tasks in isolated Docker containers), AI behavior can be unpredictable and may lead to unintended consequences.**

**By using this project, you acknowledge and accept that:**
- AI-generated commands may have bugs, security vulnerabilities, or unexpected side effects
- The agent has access to your configured tokens (Slack, GitHub - make sure you give the agent it's own GitHub account) and can interact with external services
- Tasks run in Docker containers but may still pose risks if misconfigured
- You are solely responsible for securing your environment and monitoring agent activity
- The author(s) assume no liability for any damages, data loss, or security incidents

**Use at your own risk. Test thoroughly in a controlled environment before production use.**

## Prerequisites & System Requirements

### Hardware Requirements
- **Raspberry Pi Model:** Pi 5 (8GB+ RAM recommended) or Pi 4 (4GB+ RAM minimum)
- **Storage:** NVMe M.2 SSD via PCIe (Gen 3 for Pi 5) or high-quality microSD card
- **Cooling:** Active cooling (fan) required for sustained workloads
- **Power:** Official Raspberry Pi USB-C power supply (27W for Pi 5, 15W for Pi 4)
- **Optional:** SSD1306 OLED display (I2C address 0x3C) for dashboard

### Software Requirements
- **Operating System:** Raspberry Pi OS (64-bit, Bookworm or newer)
- **Connectivity:** Network access for downloading packages
- **.NET SDK:** Version 10
- **Docker:** Required for LUNA Agent task isolation


### Recommended Preparation
 - Flash Raspberry Pi OS (64-bit) using Raspberry Pi Imager
 - Enable SSH during imaging for headless setup
 - Configure Wi-Fi credentials if not using ethernet
 - Hardware: PCIe Gen 3 & NVMe Optimization
   Force the Pi 5 to use high-speed PCIe Gen 3 and initialize I2C.
   - `sudo nano /boot/firmware/config.txt`
   - Append these lines:
     ```ini
     # Force PCIe Gen 3 for high-speed NVMe access
     dtparam=pcie_aspm=off
     dtparam=pciex1_gen=3
     # Enable Hardware I2C for Dashboard
     dtparam=i2c_arm=on
     ```
   - Set NVMe as the primary boot target:
     ```bash
     sudo rpi-eeprom-config --edit
     # Ensure BOOT_ORDER=0xf416 (attempts NVMe boot before SD)
     ```

 - Boot the Pi and ensure it's updated:
   ```bash
   sudo apt update && sudo apt full-upgrade -y
   sudo reboot
   ```
 - Install SkiaSharp dependencies for the OLED display:
   ```bash
   sudo apt install libfontconfig1 libfreetype6 libglib2.0-0t64 libgobject-2.0-0 libpango-1.0-0 libpangocairo-1.0-0 libcairo2
   ```
 - Set hostname to LUNAHOST:
   ```bash
   sudo hostnamectl set-hostname LUNAHOST
   ```



### Install Docker (as root):

Docker is required for task isolation. Install it before creating the luna user:

```bash
# Install Docker
curl -fsSL https://get.docker.com -o get-docker.sh
sudo sh get-docker.sh
rm get-docker.sh

# Add current root-user to docker group
sudo usermod -aG docker $USER

# Enable Docker service
sudo systemctl enable docker
sudo systemctl start docker

# Verify Docker installation
docker --version
docker ps
```

### Install Ollama (as root):

Ollama provides the local AI runtime for the LUNA Agent. Pull the model after the install:

```bash
# Install Ollama AI runtime
curl -fsSL https://ollama.com/install.sh | sh

# Pull the Gemma3 4B model
ollama pull gemma3:4b

# Verify Ollama is running
ollama list  # Should list gemma3:4b among installed models

# Enable and start Ollama service
sudo systemctl enable ollama
sudo systemctl start ollama

# Check Ollama status
sudo systemctl status ollama
```

### Install and Configure UFW Firewall (as root):

UFW provides essential firewall protections:

```bash
# Install UFW
sudo apt install ufw -y

# Default policies: deny incoming, allow outgoing
sudo ufw default deny incoming
sudo ufw default allow outgoing

# Allow SSH (required for remote access)
sudo ufw allow ssh

# Allow Ollama (port 11434) from localhost only
sudo ufw allow from 127.0.0.1 to any port 11434

# Enable UFW
sudo ufw enable

# Verify firewall status
sudo ufw status verbose
```

### Install GitHub CLI (as root):

GitHub CLI (gh) is used by the LUNA Agent for automatic PR creation:

```bash
sudo apt install gh -y
```
Do not sign in yet. You need to create the luna non-root-user, the agent dedicated github, and then sign in with those accounts.

### Create the dedicated LUNA non-root-user (as root):
   ```bash
   # Create user with home directory and bash shell
   sudo useradd -m -s /bin/bash luna

   # Set a password for the user
   sudo passwd luna

   # Add to docker group (for LUNA Agent container management)
   sudo usermod -aG docker luna

   # Add to hardware access groups (for I2C, GPIO, etc.)
   sudo usermod -aG gpio,i2c luna

   # Switch to the new user for remaining setup
   su - luna
   ```

	**For all remaining operatios, use the 'luna' user. Luna does not have root permissions and using that account will ensure that luna owns the app, files, etc. that will be setup.

- ** ‼️ Sign in as the luna user ‼️ **

### Download and install .NET 10.0:

```
curl -sSL https://dot.net/v1/dotnet-install.sh | bash /dev/stdin --channel 10.0
```

This command downloads the official .NET installation script from Microsoft and pipes it directly to bash. The `--channel 10.0` parameter specifies that you want .NET 10.0, the latest long-term support (LTS) version at the time of writing. The script handles downloading the correct ARM binaries for your Raspberry Pi and installs them to `~/.dotnet` in your home directory.

The `-sSL` flags tell curl to run silently (`-s`), show errors if they occur (`-S`), and follow redirects (`-L`). This ensures the download works smoothly without cluttering your terminal with progress information.

Configuring Your Environment

After the installation completes, you need to configure your shell environment so it knows where to find the .NET tools. This involves setting two environment variables: `DOTNET_ROOT` and updating your `PATH`.

Set the `DOTNET_ROOT` environment variable:

```
echo 'export DOTNET_ROOT=$HOME/.dotnet' >> ~/.bashrc
```

The `DOTNET_ROOT` variable tells .NET where its installation directory is located. This is important for the runtime to find necessary libraries and configuration files.

Add .NET to your `PATH`:

```
echo 'export PATH=$PATH:$HOME/.dotnet' >> ~/.bashrc
```

Adding `~/.dotnet` to your `PATH` allows you to run `dotnet` commands from any directory in your terminal. Without this, you'd need to type the full path to the dotnet executable every time.

Both commands append the export statements to your `~/.bashrc` file, which is the configuration file that runs every time you start a new bash session.

Apply the changes to your current session:

```
source ~/.bashrc
```

### Create the luna GitHub account

Create a dedicated GitHub account for the Pi to handle automated pushes (e.g., for code deployments or updates). This isolates the Pi's access from your personal account.

- Go to https://github.com and create a new account (e.g., `ailunamachine`).
- Verify the email and set up basic security (enable 2FA if possible).
- Invite this account as a collaborator to the repos it needs access to (grant push access where required).
- Note the account's username for later use.

### Create GitHub SSH key for the system to access git

Run these commands on the Pi (as `luna`, not root) to generate the SSH key the Pi will use to push code to GitHub:

```bash
ssh-keygen -t ed25519 -C "ailunamachine"
# Press Enter to accept the default location (~/.ssh/id_ed25519)
# Press Enter twice to skip the passphrase (or set one if you prefer)
```

Copy the public key to GitHub:

```bash
cat ~/.ssh/id_ed25519.pub
```

Add it in GitHub: Settings > SSH and GPG keys > New SSH key. Give it a descriptive title like "LUNA Pi Agent Key".

Test the connection:
```bash
ssh -T git@github.com
# Should output: Hi <username>! You've successfully authenticated, but GitHub does not provide shell access.
```

### Create GitHub Personal Access Token (PAT)

The LUNA Agent uses a Personal Access Token (PAT) to create pull requests. The token is stored in your `.env` file and used by the agent automatically.

- In GitHub (as `ailunamachine`), go to Settings > Developer settings > Personal access tokens > Tokens (classic) and generate a new token.
- Give it a name like "LUNA Agent Token" and enable scopes: `repo`, `workflow`.
- Copy the token (it starts with `ghp_`) and keep it secure.
- You will add this token to `~/.luna/luna.env` as `GH_TOKEN=ghp_...`

**Note:** You do NOT need to run `gh auth login` - the agent uses the token from the `.env` file directly.

### Fork the LUNA repository

- On GitHub, open https://github.com/dahln/LUNA and click the "Fork" button.
- Select the destination account (your user or the `ailunamachine` account).
- Note the fork URL, e.g. `https://github.com/<YOUR_USERNAME>/LUNA`.

### Clone the LUNA repo (on the Pi)

On the Pi (as `luna`), clone your fork:

```bash
git clone https://github.com/<YOUR_USERNAME>/LUNA.git ~/luna
cd ~/luna
```

### Slack App Manifest
Create a new app at https://api.slack.com/apps -> From Manifest:

```json
{
   "display_information": { "name": "L.U.N.A." },
   "features": {
      "app_home": { "home_tab_enabled": true, "messages_tab_enabled": true },
      "bot_user": { "display_name": "LUNA", "always_online": true }
   },
   "oauth_config": {
      "scopes": { "bot": ["chat:write", "im:history", "app_mentions:read", "files:write"] }
   },
   "settings": { "socket_mode_enabled": true }
}
```
*Save your **Bot Token** (`xoxb`) and **App-Level Token** (`xapp`).*

To find these tokens after creating your app:

- Go to [api.slack.com/apps](https://api.slack.com/apps) and sign in.
- Select the "L.U.N.A." app.
- **For the Bot Token (`xoxb`)**:
   - Navigate to **OAuth & Permissions**.
   - Copy the **Bot User OAuth Token** (starts with `xoxb-`).
- **For the App-Level Token (`xapp`)**:
   - Navigate to **Basic Information**.
   - Under **App-Level Tokens**, generate one if needed (name it "LUNA App Token", add scopes like `connections:write`).
   - Copy the token (starts with `xapp-`).

Store these securely and never share them publicly.

**To get the SLACK_CHANNEL_ID:**
- Open Slack and navigate to the channel you want to use.
- Right-click on the channel name in the sidebar.
- Select "Copy link".
- The channel ID is the part of the URL after the last slash (it starts with "C", e.g., `C01234567`).

**Note:** If you don't have a suitable channel yet, create a new one in Slack (e.g., #agent) and invite the LUNA bot by typing `/invite @LUNA` in the channel. The bot needs to be invited to any channel it will post to.



### Populate `~/.luna/luna.env`

As the luna user, copy the template and edit with your tokens:

```bash
mkdir -p ~/.luna
cp luna.env.template ~/.luna/luna.env
nano ~/.luna/luna.env  # Edit with your actual token values
chmod 600 ~/.luna/luna.env  # Secure the file (readable only by luna user)
```

Example content:

```env
SLACK_BOT_TOKEN=xoxb-...
SLACK_APP_TOKEN=xapp-...
SLACK_CHANNEL_ID=C01234567
GH_TOKEN=ghp_...
```

The systemd service will load this file from your home directory.


### LUNA Agent Service Setup

The LUNA Agent is an autonomous agentic flow that interacts with users via Slack, executes commands in isolated Docker containers, creates files, writes code, and manages tasks with AI assistance.

**Security Model:**
- Agent runs **WITHOUT root access** on the host system
- All task execution happens in **isolated Docker containers**
- Agent cannot install software on host system
- Files created by tasks are isolated in containers
- Containers are automatically created and cleaned up for each task
- Slack and GitHub integration work through the agent (not containers)
- **Tasks can use HTTP clients within containers** for web searches and data collection

**Prerequisites (all installed earlier as root):**
- Docker installed and running
- UFW firewall installed and configured  
- Ollama installed and running locally with gemma3:4b model pulled
- Slack app configured with Socket Mode enabled
- Git configured with SSH key for GitHub access (see GitHub section)
- LUNA configuration file `/.luna/luna.env` created from `luna.env.template`
- GitHub CLI (gh) installed for automatic PR creation: `sudo apt install gh`


**Verify Prerequisites:**
```bash
# Check Docker
docker --version
docker ps

# Check Ollama
ollama list

# Check LUNA configuration
cat ~/.luna/luna.env

# Check Git/SSH (should output: "Hi <username>! You've successfully authenticated...")
ssh -T git@github.com

# Verify .env has all required tokens
grep -E "SLACK_BOT_TOKEN|SLACK_APP_TOKEN|SLACK_CHANNEL_ID|GH_TOKEN" ~/.luna/luna.env
```

**Note:** UFW firewall status was configured by root earlier and cannot be checked by the luna user (no sudo access needed).

**Create the systemd service (as root):**

⚠️ **Important:** The following steps require root access. Switch to a root terminal or have a root user perform these commands. Luna user cannot do this (and should not have sudo access).

```bash
sudo nano /etc/systemd/system/luna.service
```

Add the following configuration:
```ini
[Unit]
Description=L.U.N.A. Autonomous Agent
After=network-online.target ollama.service
Wants=network-online.target
Requires=ollama.service

[Service]
Type=simple
User=luna
Group=luna
WorkingDirectory=/home/luna/luna
Environment=DOTNET_ROOT=/home/luna/.dotnet
Environment=PATH=/home/luna/.dotnet:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin
Environment=HOME=/home/luna

# Load LUNA configuration from user home directory
EnvironmentFile=/home/luna/.luna/luna.env

# Start the agent
ExecStart=/home/luna/.dotnet/dotnet luna-agent.cs

# Restart policy
Restart=on-failure
RestartSec=30s

# Resource limits (agent process only - Ollama runs separately and gets rest of system memory)
# Pi 5 16GB: MemoryMax=2G, CPUQuota=25%  (Ollama ~6-8GB, Agent ~2GB, System ~2-4GB, agent is I/O-bound)
# Pi 5 8GB:  MemoryMax=1G, CPUQuota=25%  (Ollama ~6-8GB is tight, Agent ~1GB, System ~1GB)
# Pi 4 4GB:  MemoryMax=500M, CPUQuota=25% (Very constrained - monitor performance)
MemoryMax=2G
CPUQuota=25%

# Security: Agent does NOT have root access on host
# Agent runs with docker group membership to create containers
# All task execution happens inside isolated Docker containers
NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=strict
ProtectHome=read-only
ReadWritePaths=/home/luna/luna
ReadWritePaths=/home/luna/.local/share/dotnet
ReadWritePaths=/home/luna/.nuget
ReadWritePaths=/tmp

# Supplementary groups (docker group for container access)
SupplementaryGroups=docker

# Allow network access for Ollama, Slack, and GitHub
PrivateNetwork=false

# Logging
StandardOutput=journal
StandardError=journal
SyslogIdentifier=luna

[Install]
WantedBy=multi-user.target
```

**Activate the service (as root):**

```bash
# Reload systemd to recognize the new service
sudo systemctl daemon-reload

# Enable service to start on boot
sudo systemctl enable luna

# Start the service now
sudo systemctl start luna

# Check service status
sudo systemctl status luna

# View live logs (you can run this as luna or root)
sudo journalctl -u luna -f
```

**Using the LUNA Agent:**

Once the service is running, interact with the agent via Slack in the `#agent` channel:

**Available Commands:**
- `/status` - Show current task and queue status
- `/details <task_id>` - Get detailed information about a specific task
- `/queue` - List all queued and paused tasks
- `/pause <task_id>` - Pause a queued task
- `/start <task_id>` - Start a task (pauses current task if running, starts or resumes specified task)
- `/stop <task_id>` - Stop a running or queued task
- `/system` - Get current system status (CPU, RAM, temperature, Ollama)
- `/help` - Display help message with all commands

**Creating Tasks:**
Simply send a message in the `#agent` channel describing what you want the agent to do:
- "Create a Python script that calculates prime numbers"
- "Write a simple web server in Go"
- "Research the latest AI models and create a comparison table"
- "Install Node.js and create a basic Express app"

The agent will:
1. Queue your task
2. Create an isolated Docker container for the task (includes curl, wget, git, build tools)
3. Process it using AI (Ollama) with iterative refinement
4. Execute commands in the container, create files, and iterate until complete
5. **Use HTTP clients (curl/wget) for web research and data collection within container**
6. Stream progress updates and AI thoughts to Slack
7. Create branches and pull requests for code changes (if applicable)
8. Log all activities, AI responses, and agentic flow to a local SQLite database
9. Clean up the container when done

**Task Management:**
- Tasks are processed one at a time (queued execution)
- Each task is logged in `~/.luna/tasks.db`
- Full agentic flow captured in database:
  - All AI thoughts and planning steps
  - User updates and streaming messages
  - Command outputs and observations
  - Action details and results
- Agent can iterate up to 100 times per task
- Agent will ask for user input if needed
- All interactions are logged to facilitate task resume/restart

**Security Notes:**
- The agent does **NOT** have root access on the host system
- All commands run in isolated Docker containers
- Agent cannot install software on host
- Files are created in containers and copied out only for PR creation
- All executed commands are logged to database
- LUNA tokens should be kept secure in `/.luna/luna.env` (mode 600)
- Agent creates private repositories only when needed
- Review pull requests before merging agent-generated code
- **Important:** Do not include sensitive information (passwords, API keys, etc.) in task descriptions as they may be visible in PR titles and commit messages
- Containers are ephemeral and cleaned up after task completion

**Example Usage Scenarios:**

1. **Create a Python Script:**
   ```
   User in Slack: Create a Python script that fetches weather data from an API and displays it
   ```
   Agent will:
   - Create a working directory
   - Write the Python script
   - Install required dependencies (requests, etc.)
   - Test the script
   - Create a branch and PR with the code

2. **Install System Tools:**
   ```
   User in Slack: Install git-lfs and configure it for this repository
   ```
   Agent will:
   - Run apt-get update
   - Install git-lfs
   - Run git lfs install
   - Verify installation
   - Report completion

3. **Research and Document:**
   ```
   User in Slack: Research the latest trends in AI agents and create a markdown summary
   ```
   Agent will:
   - Use Ollama to research the topic
   - Compile findings into a document
   - Create a formatted markdown file
   - Create a PR with the documentation

4. **Multi-step Development:**
   ```
   User in Slack: Create a Go web server that serves a simple REST API with CRUD operations
   ```
   Agent will:
   - Create project structure
   - Write Go code for the server
   - Create handlers for CRUD operations
   - Add basic tests
   - Create documentation
   - Create a branch and PR

5. **Web Research and Data Collection:**
   ```
   User in Slack: Research the latest Python web frameworks and create a comparison table
   ```
   Agent will:
   - Use HTTP client within Docker container to search for information
   - Compile findings from multiple sources
   - Create a formatted markdown table
   - Include pros, cons, and use cases
   - Create a PR with the documentation

**Monitoring Tasks:**
```bash
# Check agent logs
sudo journalctl -u luna -f

# View database directly
sqlite3 ~/.luna/tasks.db "SELECT Id, Status, Description FROM Tasks ORDER BY CreatedAt DESC LIMIT 10;"

# Check agent status
sudo systemctl status luna
```


## 5. OLED Screen Service:

```bash
sudo nano /etc/systemd/system/dashboard.service
```

Add the following configuration:
```ini
[Unit]
Description=L.U.N.A. Dashboard Screen

[Service]
Type=simple
User=luna
Group=luna
WorkingDirectory=/home/luna/luna
Environment=DOTNET_ROOT=/home/luna/.dotnet
Environment=PATH=/home/luna/.dotnet:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin

# Start the dashboard
ExecStart=/home/luna/.dotnet/dotnet screen.cs

# Restart policy
Restart=on-failure
RestartSec=10s

# Resource limits (lightweight service)
MemoryMax=512M
CPUQuota=25%

# Security hardening
NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=strict
ProtectHome=read-only
ReadWritePaths=/home/luna/luna
ReadWritePaths=/home/luna/.local/share/dotnet
ReadWritePaths=/home/luna/.nuget

# Logging
StandardOutput=journal
StandardError=journal
SyslogIdentifier=luna

[Install]
WantedBy=multi-user.target
```

**Activate the service:**
```bash
# Reload systemd to recognize the new service
sudo systemctl daemon-reload

# Enable service to start on boot
sudo systemctl enable luna

# Start the service now
sudo systemctl start luna

# Check service status
sudo systemctl status luna

# View live logs
sudo journalctl -u luna -f
```


If all has gona as expected, all the dependences are intalled and accessible by luna (the user) and luna (the agent process), you should be able to send messages to Luna via Slack.


## Issues & Support

If you encounter any issues, bugs, or have feature requests:

1. **Check the README** - Ensure you've followed all setup steps correctly
2. **Review logs** - Use `sudo journalctl -u luna -f` to check for errors
3. **Create an Issue** - Open a GitHub issue with:
   - Detailed description of the problem
   - Steps to reproduce
   - Your system setup (Pi model or Non-Pi if applicable, OS version, etc.)
   - Relevant log output (redact any sensitive information)

I'll review and respond to issues as time permits. This is an experimental project, so please be patient!


## Contributing

This project is open-source under the MIT License. Feel free to fork, modify, and submit pull requests. Please ensure any changes maintain the security model and don't introduce vulnerabilities.
