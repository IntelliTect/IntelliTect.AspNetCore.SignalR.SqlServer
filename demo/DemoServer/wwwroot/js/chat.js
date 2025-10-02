"use strict";

function setupHub(suffix) {
    var connection = new signalR.HubConnectionBuilder().withUrl("/chatHub" + suffix).build();

    //Disable send button until connection is established
    document.getElementById("sendButton" + suffix).disabled = true;

    connection.on("BroadcastMessage", function (user, message) {
        var li = document.createElement("li");
        document.getElementById("messagesList" + suffix).appendChild(li);
        // We can assign user-supplied strings to an element's textContent because it
        // is not interpreted as markup. If you're assigning in any other way, you 
        // should be aware of possible script injection concerns.
        li.textContent = `${user} says ${message}`;
    });

    connection.start().then(function () {
        document.getElementById("sendButton" + suffix).disabled = false;
    }).catch(function (err) {
        return console.error(err.toString());
    });

    document.getElementById("sendButton" + suffix).addEventListener("click", function (event) {
        var user = document.getElementById("userInput" + suffix).value;
        var message = document.getElementById("messageInput" + suffix).value;
        connection.invoke("SendMessage", user, message).catch(function (err) {
            return console.error(err.toString());
        });
        event.preventDefault();
    });
}

// Setup broadcast button functionality
document.addEventListener("DOMContentLoaded", function () {
    document.getElementById("broadcastButton").addEventListener("click", async function (event) {
        const messageInput = document.getElementById("serverMessageInput");
        const message = messageInput.value.trim();
        
        if (!message) {
            alert("Please enter a message to broadcast");
            return;
        }

        try {
            const response = await fetch("/api/broadcast", {
                method: "POST",
                headers: {
                    "Content-Type": "application/json"
                },
                body: JSON.stringify({ message: message })
            });

            if (response.ok) {
                const result = await response.json();
                console.log("Broadcast successful:", result);
            } else {
                console.error("Broadcast failed:", response.statusText);
                alert("Failed to broadcast message");
            }
        } catch (error) {
            console.error("Error broadcasting message:", error);
            alert("Error broadcasting message");
        }
        
        event.preventDefault();
    });
});