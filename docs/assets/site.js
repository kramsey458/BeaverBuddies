// Optional enhancement: "Copy" buttons next to file paths. The site works without it.
document.querySelectorAll('.copy').forEach(function (button) {
  var target = button.parentElement.querySelector('code');
  if (!target || !navigator.clipboard) { button.hidden = true; return; }
  button.addEventListener('click', function () {
    navigator.clipboard.writeText(target.textContent.trim()).then(function () {
      var original = button.textContent;
      button.textContent = 'Copied';
      setTimeout(function () { button.textContent = original; }, 1600);
    });
  });
});
