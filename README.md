[job_applicator_readme (1).md](https://github.com/user-attachments/files/34998291/job_applicator_readme.1.md)
# Job Applicator (`job-applicator`)

An automated job application pipeline and browser automation tool designed to streamline searching, tracking, and submitting job applications across modern portals.

---

## Features

* **Automated Form Navigation:** Leverages robust browser automation scripts to handle standard input fields, dropdowns, and multi-step application workflows.
* **Persistent Session Management:** Utilizes persistent browser user profiles to preserve authentication states, cookies, and session data, minimizing repetitive logins and bot-detection friction.
* **Modular Pipeline Architecture:** Built with decoupled directories to easily handle scraping, application execution, and tracking utilities.

---

## Project Structure

```text
job-applicator/
├── scraper/         # Job search board scrapers and listing parsers
├── applicator/      # Core automation scripts for filling and submitting applications
├── config/          # Environment templates, user profiles, and form field maps
├── utils/           # Logging, database connectors, and helper utilities
├── main.py          # Main entry point orchestrating the pipeline
├── requirements.txt # Python dependencies
└── README.md
```

---

## Getting Started

### Prerequisites

* Python 3.10 or higher
* Playwright (Python package)

### Installation

1. **Clone the repository:**
   ```bash
   git clone https://github.com/coffeeslayer/job-applicator.git
   cd job-applicator
   ```

2. **Create and activate a virtual environment:**
   ```bash
   python -m venv .venv
   # On Windows (Bash / Git Bash):
   source .venv/Scripts/activate
   # On macOS / Linux:
   source .venv/bin/activate
   ```

3. **Install dependencies and browser binaries:**
   ```bash
   pip install -r requirements.txt
   playwright install chromium
   ```

4. **Configure environment variables:**
   Create a `.env` file in the root directory and populate your credentials, target criteria, and file paths.

---

## How to Run Each Directory / Component

Because this project is structured modularly, you can run or test specific sub-components independently depending on your workflow:

### 1. Running the Main Pipeline (`main.py`)
To execute the full end-to-end job discovery and application process:
```bash
python main.py
```

### 2. Running the Scraper Module (`scraper/`)
If you want to test or run job board scrapers in isolation to fetch fresh listings without applying:
```bash
python -m scraper.main
# Or if it uses a specific run script inside the directory:
python scraper/runner.py
```

### 3. Running the Applicator Module (`applicator/`)
To test form-filling logic or execute submissions against a pre-loaded list of target job URLs:
```bash
python -m applicator.engine
# Or run individual test scripts if provided:
python applicator/submitter.py
```

### 4. Configuration & Utilities (`config/` & `utils/`)
* **`config/`**: Store your `.env`, resume file paths, and JSON/YAML field mappings here before running the scripts.
* **`utils/`**: Contains shared helper modules imported by `scraper/` and `applicator/`.

---

## Contributing

Contributions, bug reports, and feature requests are welcome! Feel free to open an issue or submit a pull request.

---

## License

This project is licensed under the terms of the [MIT License](LICENSE).
